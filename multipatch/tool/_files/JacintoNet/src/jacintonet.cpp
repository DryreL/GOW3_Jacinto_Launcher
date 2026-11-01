// JacintoNet - wsock32.dll proxy for Gears 3 Jacinto v1.1.1 (Multi Patch Tool).
// GearGame.exe imports WSOCK32.dll, so this file placed next to it is loaded at startup.
// Every export behaves like the system wsock32 (see wsock32.def). On top of that it starts Steam as Spacewar (480).
// Phase 2a: Steam init, rich presence, log.
// Phase 2b: UDP tunnel. Every Steam friend has a virtual IP 10.a.b.c (low 24 bits of the account ID). Game packets sent to
//           such an address go through Steam P2P (ISteamNetworkingMessages, relayed, no port forwarding) and come back
//           to the game from recvfrom as if they came from that address. All other traffic is untouched.
// Phase 2c: every player owns a friends-only Steam lobby (version + port in its data). F8 or the overlay invites a friend;
//           accepting the invite joins the host's lobby, checks the version and sends the game to the host's virtual IP
//           through UGameEngine::SetClientTravel (what the console "open" command does).
// 2d:       rich presence shows the act / chapter (host: from its Launch.log, joined players: from the host's lobby);
//           the lobby carries the GearGame.u package GUID (must match) and file hash (a difference is only logged).
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <cstdlib>
#include <share.h>
#include <deque>
#include <map>
#include <set>
#include <string>
#include <vector>
#include "steam/steam_api.h"
#include "steam/isteamnetworkingmessages.h"
#include "steam/isteamnetworkingutils.h"

static FILE* g_log;
static const char kVersion[] = "0.3.8";                // Multi Patch Tool version; players must match to join

static void Log(const char* fmt, ...)
{
    if (!g_log) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(g_log, "[%02d:%02d:%02d.%03d] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a; va_start(a, fmt); vfprintf(g_log, fmt, a); va_end(a);
    fputc('\n', g_log); fflush(g_log);
}

// ---- The 4 functions the system wsock32 implements itself: call its own code ----
static HMODULE RealWsock()
{
    static HMODULE h = [] {
        wchar_t p[MAX_PATH]; GetSystemDirectoryW(p, MAX_PATH);
        wcscat_s(p, L"\\wsock32.dll");
        return LoadLibraryW(p);
    }();
    return h;
}
template <class F> static F Real(int ordinal) { return (F)GetProcAddress(RealWsock(), MAKEINTRESOURCEA(ordinal)); }

// ---- Tunnel state ----
static volatile bool g_ready;                          // Steam is up; set once by SteamThread
static SRWLOCK g_peerLock = SRWLOCK_INIT;
static std::map<uint32_t, uint64_t> g_peers;           // virtual IP (host order) -> SteamID64
static std::set<uint64_t> g_seen;                      // peers already logged once
struct Packet { uint32_t ip; uint16_t srcPort; std::vector<char> data; };
static std::map<uint16_t, std::deque<Packet>> g_inbox; // local destination port -> packets
static SRWLOCK g_inboxLock = SRWLOCK_INIT;              // recvfrom may run on more than one game thread
static const int kChannel = 0;
static const uint32_t kMaxQueued = 512;                // per port; older packets are dropped beyond this

static uint32_t VirtualIp(CSteamID id)                 // 10.a.b.c from the low 24 bits of the account ID
{
    // ponytail: two friends whose account IDs share the low 24 bits would collide; a lobby-assigned table fixes that if it ever matters.
    return 0x0A000000u | (id.GetAccountID() & 0x00FFFFFFu);
}
static bool IsVirtual(uint32_t ipHost) { return (ipHost >> 24) == 10; }

static void AddPeer(CSteamID id)
{
    AcquireSRWLockExclusive(&g_peerLock);
    g_peers[VirtualIp(id)] = id.ConvertToUint64();
    ReleaseSRWLockExclusive(&g_peerLock);
}
static bool FindPeer(uint32_t ipHost, uint64_t& out)
{
    AcquireSRWLockShared(&g_peerLock);
    auto it = g_peers.find(ipHost);
    bool ok = it != g_peers.end();
    if (ok) out = it->second;
    ReleaseSRWLockShared(&g_peerLock);
    return ok;
}
static void LoadFriends()
{
    int n = SteamFriends()->GetFriendCount(k_EFriendFlagImmediate);
    for (int i = 0; i < n; i++) AddPeer(SteamFriends()->GetFriendByIndex(i, k_EFriendFlagImmediate));
    Log("Friends mapped to virtual IPs: %d", n);
}
static const char* IpStr(uint32_t ipHost, char* buf)
{
    sprintf_s(buf, 16, "%u.%u.%u.%u", ipHost >> 24, (ipHost >> 16) & 255, (ipHost >> 8) & 255, ipHost & 255);
    return buf;
}
static uint16_t LocalPort(SOCKET s)
{
    sockaddr_in a = {}; int len = sizeof(a);
    return getsockname(s, (sockaddr*)&a, &len) == 0 ? ntohs(a.sin_port) : 0;
}

// Pull everything Steam received into the per-port inbox (called from the game thread inside recvfrom).
static void PumpSteam()
{
    SteamNetworkingMessage_t* msgs[32];
    int n;
    while ((n = SteamNetworkingMessages()->ReceiveMessagesOnChannel(kChannel, msgs, 32)) > 0) {
        for (int i = 0; i < n; i++) {
            SteamNetworkingMessage_t* m = msgs[i];
            if (m->m_cbSize > 4) {
                const uint8_t* d = (const uint8_t*)m->m_pData;
                CSteamID from = m->m_identityPeer.GetSteamID();
                AddPeer(from);                                   // reply path, also for non-friends we accepted
                Packet p;
                p.ip = VirtualIp(from);
                p.srcPort = (uint16_t)(d[0] << 8 | d[1]);
                uint16_t dst = (uint16_t)(d[2] << 8 | d[3]);
                p.data.assign((const char*)d + 4, (const char*)d + m->m_cbSize);
                auto& q = g_inbox[dst];
                if (q.size() >= kMaxQueued) q.pop_front();
                q.push_back(std::move(p));
                if (g_seen.insert(from.ConvertToUint64()).second) {
                    char b[16]; Log("First packet from %s (%s) to local port %u", SteamFriends()->GetFriendPersonaName(from), IpStr(VirtualIp(from), b), dst);
                }
            }
            m->Release();
        }
    }
}

// ---- Game travel (join a host) ----
// RVAs from Binaries\Win64\GearGame.pdb (GearGame.exe built 2011-09-14, TimeDateStamp 0x4e704ed6).
// UGameEngine::SetClientTravel(this, const TCHAR* URL, ETravelType) only stores the URL; the engine travels on its next tick.
static const uint32_t kRvaGEngine = 0x542cdd8;
static const uint32_t kRvaSetClientTravel = 0x93d980;
static const uint8_t kSetClientTravelStart[16] = { 0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20,0x48,0x8b,0xd9,0x48,0x81,0xc1 };
typedef void (*SetClientTravelFn)(void* engine, const wchar_t* url, int travelType);

static DWORD g_gameThread;                             // main thread: loads this DLL and runs the UE3 game loop
static SRWLOCK g_travelLock = SRWLOCK_INIT;
static wchar_t g_travelUrl[64];                        // pending join, set by Steam callbacks
static volatile bool g_travelPending;

static void RequestTravel(const wchar_t* url)
{
    AcquireSRWLockExclusive(&g_travelLock);
    wcscpy_s(g_travelUrl, url);
    g_travelPending = true;
    ReleaseSRWLockExclusive(&g_travelLock);
}
// Game thread only (called from recvfrom, which the net driver polls every tick in the menu and in game).
static void DoPendingTravel()
{
    wchar_t url[64];
    AcquireSRWLockExclusive(&g_travelLock);
    wcscpy_s(url, g_travelUrl);
    g_travelPending = false;
    ReleaseSRWLockExclusive(&g_travelLock);

    uint8_t* base = (uint8_t*)GetModuleHandleW(nullptr);
    if (memcmp(base + kRvaSetClientTravel, kSetClientTravelStart, sizeof(kSetClientTravelStart)) != 0) {
        Log("Join failed: unknown GearGame.exe (SetClientTravel not found). Type in the console: open %ls", url);
        return;
    }
    void* engine = *(void**)(base + kRvaGEngine);
    if (!engine) { Log("Join failed: GEngine not ready"); return; }
    ((SetClientTravelFn)(base + kRvaSetClientTravel))(engine, url, 0 /* TRAVEL_Absolute */);
    Log("Travelling to %ls", url);
}

extern "C" {
int WSAAPI J_getsockopt(SOCKET s, int level, int name, char* val, int* len)
{
    static auto f = Real<decltype(&J_getsockopt)>(7);
    return f(s, level, name, val, len);
}
int WSAAPI J_recv(SOCKET s, char* buf, int len, int flags)
{
    static auto f = Real<decltype(&J_recv)>(16);
    return f(s, buf, len, flags);
}
int WSAAPI J_recvfrom(SOCKET s, char* buf, int len, int flags, sockaddr* from, int* fromlen)
{
    static auto f = Real<decltype(&J_recvfrom)>(17);
    if (g_travelPending && GetCurrentThreadId() == g_gameThread) DoPendingTravel();
    if (g_ready) {
        AcquireSRWLockExclusive(&g_inboxLock);
        PumpSteam();
        bool have = false; Packet p;
        if (!g_inbox.empty()) {
            auto it = g_inbox.find(LocalPort(s));
            if (it != g_inbox.end() && !it->second.empty()) {
                p = std::move(it->second.front());
                it->second.pop_front();
                have = true;
            }
        }
        ReleaseSRWLockExclusive(&g_inboxLock);
        if (have) {
            int n = (int)p.data.size() < len ? (int)p.data.size() : len;
            memcpy(buf, p.data.data(), n);
            if (from && fromlen && *fromlen >= (int)sizeof(sockaddr_in)) {
                sockaddr_in* a = (sockaddr_in*)from;
                memset(a, 0, sizeof(*a));
                a->sin_family = AF_INET;
                a->sin_addr.s_addr = htonl(p.ip);
                a->sin_port = htons(p.srcPort);
                *fromlen = sizeof(sockaddr_in);
            }
            return n;
        }
    }
    return f(s, buf, len, flags, from, fromlen);
}
int WSAAPI J_sendto(SOCKET s, const char* buf, int len, int flags, const sockaddr* to, int tolen)
{
    if (g_ready && to && tolen >= (int)sizeof(sockaddr_in) && to->sa_family == AF_INET) {
        const sockaddr_in* a = (const sockaddr_in*)to;
        uint32_t ip = ntohl(a->sin_addr.s_addr);
        uint64_t peer;
        if (IsVirtual(ip) && FindPeer(ip, peer)) {
            uint16_t src = LocalPort(s), dst = ntohs(a->sin_port);
            std::vector<uint8_t> pkt(4 + len);
            pkt[0] = (uint8_t)(src >> 8); pkt[1] = (uint8_t)src; pkt[2] = (uint8_t)(dst >> 8); pkt[3] = (uint8_t)dst;
            memcpy(pkt.data() + 4, buf, len);
            SteamNetworkingIdentity id; id.SetSteamID64(peer);
            EResult r = SteamNetworkingMessages()->SendMessageToUser(id, pkt.data(), (uint32)pkt.size(),
                k_nSteamNetworkingSend_UnreliableNoNagle | k_nSteamNetworkingSend_AutoRestartBrokenSession, kChannel);
            if (r != k_EResultOK && r != k_EResultNoConnection) { char b[16]; Log("SendMessageToUser %s failed: %d", IpStr(ip, b), (int)r); }
            return len;                                          // UDP semantics: report sent, Steam may still drop it
        }
    }
    return sendto(s, buf, len, flags, to, tolen);                // ws2_32, what the system wsock32 forwards to
}
int WSAAPI J_setsockopt(SOCKET s, int level, int name, const char* val, int len)
{
    static auto f = Real<decltype(&J_setsockopt)>(21);
    return f(s, level, name, val, len);
}
}

// ---- GearGame.u identity (lobby data) ----
// The package GUID is what the engine compares when a client connects: a different GUID cannot join at all.
// The file hash also covers the installer's same-size script patches (K&M or gamepad choice etc.), which still play together.
static char g_pkgGuid[33] = "unknown", g_pkgHash[17] = "unknown";
static void ReadGearGameU(const wchar_t* gameDir)
{
    wchar_t p[MAX_PATH]; swprintf_s(p, L"%s\\GearGame\\Script\\GearGame.u", gameDir);
    FILE* f = _wfsopen(p, L"rb", _SH_DENYNO);
    if (!f) { Log("GearGame.u not found: version check limited to the tool version"); return; }
    static unsigned char buf[1 << 20];
    uint64_t h = 1469598103934665603ull;               // FNV-1a 64
    size_t n, total = 0;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) {
        if (total == 0 && n >= 128) {                  // header: tag, ver, size, folder name (FString), flags, 11 ints, GUID
            int32_t len; memcpy(&len, buf + 12, 4);
            size_t g = 16 + (len >= 0 ? (size_t)len : (size_t)-len * 2) + 4 + 11 * 4;
            if (g + 16 <= n) for (int i = 0; i < 16; i++) sprintf_s(g_pkgGuid + i * 2, 3, "%02x", buf[g + i]);
        }
        for (size_t i = 0; i < n; i++) { h ^= buf[i]; h *= 1099511628211ull; }
        total += n;
    }
    fclose(f);
    sprintf_s(g_pkgHash, "%016llx", (unsigned long long)h);
    Log("GearGame.u: GUID %s, hash %s", g_pkgGuid, g_pkgHash);
}

// ---- Campaign chapter (rich presence) ----
// The host's game logs "CHAPTER NOW <n>" (SeqAct_DisplayChapterTitle) in Launch.log; n is the ChapterN of the campaign data.
// The host publishes it in its lobby, joined players show the host's chapter.
static int g_chapter = -1;                             // -1 = main menu / unknown
static wchar_t g_gameDir[MAX_PATH];
static void TailLaunchLog()
{
    static long long pos = -1; static std::string part;
    wchar_t p[MAX_PATH]; swprintf_s(p, L"%s\\GearGame\\Logs\\Launch.log", g_gameDir);
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (!GetFileAttributesExW(p, GetFileExInfoStandard, &fa)) return;
    FILETIME created, x1, x2, x3; GetProcessTimes(GetCurrentProcess(), &created, &x1, &x2, &x3);
    if (CompareFileTime(&fa.ftLastWriteTime, &created) < 0) return;      // still the previous session's log
    long long size = ((long long)fa.nFileSizeHigh << 32) | fa.nFileSizeLow;
    if (pos < 0 || size < pos) { pos = 0; part.clear(); }               // first look, or the game started a new log
    if (size == pos) return;
    FILE* f = _wfsopen(p, L"rb", _SH_DENYNO);
    if (!f) return;
    _fseeki64(f, pos, SEEK_SET);
    std::string chunk((size_t)(size - pos), '\0');
    chunk.resize(fread(&chunk[0], 1, chunk.size(), f));
    fclose(f);
    pos += (long long)chunk.size();
    part += chunk;
    size_t start = 0, nl;
    while ((nl = part.find('\n', start)) != std::string::npos) {
        std::string line = part.substr(start, nl - start); start = nl + 1;
        size_t k = line.find("CHAPTER NOW ");
        if (k != std::string::npos) g_chapter = atoi(line.c_str() + k + 12);
        else if (line.find("Log: LoadMap: ") != std::string::npos) {
            size_t c = line.find("?chapter=");
            if (line.find("GearStart") != std::string::npos) g_chapter = -1;
            else if (c != std::string::npos) g_chapter = atoi(line.c_str() + c + 9);   // known while the map still loads
        }
    }
    part.erase(0, start);
}

static void PresenceText(int chapter, char* out, size_t cap)
{
    // Chapter index -> act: Prologue + Act I = 0..6, Act II 7..13, Act III 14..18, Act IV 19..24, Act V 25..30.
    static const int kActStart[] = { 1, 7, 14, 19, 25, 31 };
    static const char* kActs[] = { "I", "II", "III", "IV", "V" };
    if (chapter < 0 || chapter > 30) { sprintf_s(out, cap, "Gears 3 Jacinto - Main Menu"); return; }
    wchar_t sec[48], name[64], ini[MAX_PATH];
    swprintf_s(sec, L"Chapter%d GearCampaignChapterData", chapter);
    swprintf_s(ini, L"%s\\GearGame\\Localization\\INT\\GearGame.int", g_gameDir);
    GetPrivateProfileStringW(sec, L"DisplayName", L"", name, 64, ini);
    char n8[128]; WideCharToMultiByte(CP_UTF8, 0, name, -1, n8, sizeof(n8), nullptr, nullptr);
    if (chapter == 0) { sprintf_s(out, cap, "Gears 3 Jacinto - Prologue%s%s", *n8 ? ": " : "", n8); return; }
    int a = 0; while (chapter >= kActStart[a + 1]) a++;
    sprintf_s(out, cap, "Gears 3 Jacinto - Act %s, Chapter %d%s%s", kActs[a], chapter - kActStart[a] + 1, *n8 ? ": " : "", n8);
}

// ---- Steam ----
static int g_port = 7777;                              // [URL] Port from GearEngine.ini: where this PC's game listens
static CSteamID g_lobby;                               // lobby we are in (our own, or the host's after an invite)
static bool g_inviteWhenReady;                         // F8 pressed while we were in someone else's lobby

static bool OwnLobby() { return g_lobby.IsValid() && SteamMatchmaking()->GetLobbyOwner(g_lobby) == SteamUser()->GetSteamID(); }
static void UpdatePresence()
{
    static int shown = -2, published = -2;
    static uint64_t publishedLobby;
    int chapter = g_chapter;
    if (g_lobby.IsValid() && !OwnLobby()) {            // playing in a friend's game: show the host's chapter
        const char* c = SteamMatchmaking()->GetLobbyData(g_lobby, "chapter");
        chapter = *c ? atoi(c) : -1;
    }
    else if (OwnLobby() && (published != g_chapter || publishedLobby != g_lobby.ConvertToUint64())) {
        char c[8]; sprintf_s(c, "%d", g_chapter);
        SteamMatchmaking()->SetLobbyData(g_lobby, "chapter", c);
        published = g_chapter; publishedLobby = g_lobby.ConvertToUint64();
    }
    if (chapter == shown) return;
    char text[160]; PresenceText(chapter, text, sizeof(text));
    SteamFriends()->SetRichPresence("status", text);
    shown = chapter;
    Log("Steam status: %s", text);
}

static void CreateOwnLobby() { SteamMatchmaking()->CreateLobby(k_ELobbyTypeFriendsOnly, 4); }
static void LeaveCurrentLobby()
{
    if (g_lobby.IsValid()) SteamMatchmaking()->LeaveLobby(g_lobby);
    g_lobby = k_steamIDNil;
}

struct SteamEvents {
    STEAM_CALLBACK(SteamEvents, OnSessionRequest, SteamNetworkingMessagesSessionRequest_t);
    STEAM_CALLBACK(SteamEvents, OnSessionFailed, SteamNetworkingMessagesSessionFailed_t);
    STEAM_CALLBACK(SteamEvents, OnLobbyEnter, LobbyEnter_t);
    STEAM_CALLBACK(SteamEvents, OnJoinRequested, GameLobbyJoinRequested_t);
    STEAM_CALLBACK(SteamEvents, OnLobbyData, LobbyDataUpdate_t);
    STEAM_CALLBACK(SteamEvents, OnRichJoin, GameRichPresenceJoinRequested_t);
};
// "+connect_lobby <id>" from rich presence ("Join Game" in the friends list) or from the command line.
static bool JoinLobbyFromConnect(const char* s)
{
    const char* k = s ? strstr(s, "+connect_lobby") : nullptr;
    if (!k) return false;
    CSteamID lobby(_strtoui64(k + 14, nullptr, 10));
    if (!lobby.IsValid() || lobby == g_lobby) return false;
    Log("Joining the lobby from %s", k);
    LeaveCurrentLobby();
    SteamMatchmaking()->JoinLobby(lobby);
    return true;
}
void SteamEvents::OnRichJoin(GameRichPresenceJoinRequested_t* e)
{
    Log("Join Game from %s", SteamFriends()->GetFriendPersonaName(e->m_steamIDFriend));
    JoinLobbyFromConnect(e->m_rgchConnect);
}
void SteamEvents::OnLobbyData(LobbyDataUpdate_t* e)
{
    if (e->m_ulSteamIDLobby == g_lobby.ConvertToUint64()) UpdatePresence();   // the host moved to another chapter
}
void SteamEvents::OnLobbyEnter(LobbyEnter_t* e)
{
    CSteamID lobby(e->m_ulSteamIDLobby);
    if (e->m_EChatRoomEnterResponse != k_EChatRoomEnterResponseSuccess) {
        Log("Could not enter lobby (response %u)", e->m_EChatRoomEnterResponse);
        if (!g_lobby.IsValid()) CreateOwnLobby();
        return;
    }
    g_lobby = lobby;
    char id[24]; sprintf_s(id, "%llu", lobby.ConvertToUint64());
    SteamFriends()->SetRichPresence("steam_player_group", id);
    // "Join Game" in the Steam friends list sends this string back as GameRichPresenceJoinRequested_t (OnRichJoin).
    char connect[48]; sprintf_s(connect, "+connect_lobby %s", id);
    SteamFriends()->SetRichPresence("connect", connect);
    CSteamID owner = SteamMatchmaking()->GetLobbyOwner(lobby);

    if (owner == SteamUser()->GetSteamID()) {                     // our own lobby: publish how to reach us
        char port[8]; sprintf_s(port, "%d", g_port);
        SteamMatchmaking()->SetLobbyData(lobby, "jacinto", "1");
        SteamMatchmaking()->SetLobbyData(lobby, "ver", kVersion);
        SteamMatchmaking()->SetLobbyData(lobby, "port", port);
        SteamMatchmaking()->SetLobbyData(lobby, "guid", g_pkgGuid);
        SteamMatchmaking()->SetLobbyData(lobby, "uhash", g_pkgHash);
        Log("Lobby ready. Press F8 (or Shift+Tab > friend > Invite) to invite a friend.");
        if (g_inviteWhenReady) { g_inviteWhenReady = false; SteamFriends()->ActivateGameOverlayInviteDialog(lobby); }
        return;
    }

    // Joined a friend's lobby through an invite.
    const char* ver = SteamMatchmaking()->GetLobbyData(lobby, "ver");
    const char* name = SteamFriends()->GetFriendPersonaName(owner);
    if (strcmp(SteamMatchmaking()->GetLobbyData(lobby, "jacinto"), "1") != 0 || strcmp(ver, kVersion) != 0) {
        Log("Cannot join %s: Multi Patch Tool %s there, %s here. Both players need the same version.", name, *ver ? ver : "unknown", kVersion);
        LeaveCurrentLobby();
        CreateOwnLobby();
        return;
    }
    const char* guid = SteamMatchmaking()->GetLobbyData(lobby, "guid");
    if (*guid && strcmp(g_pkgGuid, "unknown") != 0 && strcmp(guid, g_pkgGuid) != 0) {
        Log("Cannot join %s: different GearGame.u (package GUID %s there, %s here). Both players need the same game build.", name, guid, g_pkgGuid);
        LeaveCurrentLobby();
        CreateOwnLobby();
        return;
    }
    const char* uhash = SteamMatchmaking()->GetLobbyData(lobby, "uhash");
    if (*uhash && strcmp(uhash, g_pkgHash) != 0)
        Log("Note: %s's GearGame.u differs from yours (other installer options, e.g. Keyboard & Mouse / Gamepad). Joining anyway; "
            "if something behaves differently, run the same installer options on both PCs.", name);
    int port = atoi(SteamMatchmaking()->GetLobbyData(lobby, "port"));
    if (port <= 0) port = 7777;
    AddPeer(owner);
    char ip[16]; wchar_t url[64];
    swprintf_s(url, L"%hs:%d", IpStr(VirtualIp(owner), ip), port);
    Log("Joining %s (%ls)", name, url);
    RequestTravel(url);
}
void SteamEvents::OnJoinRequested(GameLobbyJoinRequested_t* e)
{
    Log("Invite accepted from %s", SteamFriends()->GetFriendPersonaName(e->m_steamIDFriend));
    LeaveCurrentLobby();
    SteamMatchmaking()->JoinLobby(e->m_steamIDLobby);
}
void SteamEvents::OnSessionRequest(SteamNetworkingMessagesSessionRequest_t* e)
{
    CSteamID who = e->m_identityRemote.GetSteamID();
    // ponytail: any Steam friend may connect (invites only go to friends anyway); lobby-members-only if strangers ever get in.
    bool ok = SteamFriends()->GetFriendRelationship(who) == k_EFriendRelationshipFriend;
    Log("Session request from %s: %s", SteamFriends()->GetFriendPersonaName(who), ok ? "accepted (friend)" : "rejected (not a friend)");
    if (ok) { AddPeer(who); SteamNetworkingMessages()->AcceptSessionWithUser(e->m_identityRemote); }
}
void SteamEvents::OnSessionFailed(SteamNetworkingMessagesSessionFailed_t* e)
{
    Log("Session with %s failed: %d %s", SteamFriends()->GetFriendPersonaName(e->m_info.m_identityRemote.GetSteamID()),
        e->m_info.m_eEndReason, e->m_info.m_szEndDebug);
}

static DWORD WINAPI SteamThread(void*)
{
    wchar_t dir[MAX_PATH]; GetModuleFileNameW(nullptr, dir, MAX_PATH);
    *wcsrchr(dir, L'\\') = 0;                                      // ...\Binaries\Win64

    wchar_t logPath[MAX_PATH]; swprintf_s(logPath, L"%s\\..\\..\\GearGame\\Logs\\JacintoNet.log", dir);
    g_log = _wfsopen(logPath, L"w", _SH_DENYNO);                   // shared: readable while the game runs
    Log("JacintoNet %s (2d) loaded", kVersion);
    wchar_t ini[MAX_PATH]; swprintf_s(ini, L"%s\\..\\..\\GearGame\\Config\\GearEngine.ini", dir);
    g_port = (int)GetPrivateProfileIntW(L"URL", L"Port", 7777, ini);
    swprintf_s(g_gameDir, L"%s\\..\\..", dir);
    ReadGearGameU(g_gameDir);

    wchar_t api[MAX_PATH]; swprintf_s(api, L"%s\\steam_api64.dll", dir);
    if (GetFileAttributesW(api) == INVALID_FILE_ATTRIBUTES) { Log("steam_api64.dll missing next to GearGame.exe: Steam features off"); return 0; }

    // ponytail: Spacewar (Valve's public test app); a real AppID would need a Steam store page.
    SetEnvironmentVariableA("SteamAppId", "480");
    SetEnvironmentVariableA("SteamGameId", "480");

    SteamErrMsg err = {};
    ESteamAPIInitResult r = SteamAPI_InitEx(&err);
    if (r != k_ESteamAPIInitResult_OK) { Log("SteamAPI_InitEx failed (%d): %s", (int)r, err); return 0; }

    CSteamID me = SteamUser()->GetSteamID();
    char b[16];
    Log("Steam OK: %s, AppID %u", SteamFriends()->GetPersonaName(), SteamUtils()->GetAppID());
    Log("Your virtual IP: %s, game port %d (manual join: open %s:%d)", IpStr(VirtualIp(me), b), g_port, b, g_port);
    Log("Overlay module %s", GetModuleHandleW(L"GameOverlayRenderer64.dll") ? "loaded" : "NOT loaded (start the game from Steam)");
    SteamNetworkingUtils()->InitRelayNetworkAccess();
    LoadFriends();

    static SteamEvents events;                                     // registers the callbacks
    g_ready = true;
    if (!JoinLobbyFromConnect(GetCommandLineA())) CreateOwnLobby();   // started by Steam to join a friend

    // F8 while the game window is in front: Steam invite dialog for our lobby. The hot key exists only while the game
    // has focus, so F8 stays free for other programs. (Polling GetAsyncKeyState got this DLL flagged as a keylogger.)
    const int kInviteHotKey = 1;
    bool hotKeyOn = false;
    for (int tick = 0;; tick++) {
        SteamAPI_RunCallbacks();

        DWORD pid = 0; GetWindowThreadProcessId(GetForegroundWindow(), &pid);
        bool focused = pid == GetCurrentProcessId();
        if (focused != hotKeyOn) {
            if (focused) { if (!RegisterHotKey(nullptr, kInviteHotKey, MOD_NOREPEAT, VK_F8)) Log("F8 is in use by another program: use Shift+Tab > Friend > Invite"); }
            else UnregisterHotKey(nullptr, kInviteHotKey);
            hotKeyOn = focused;
        }
        MSG msg;
        while (PeekMessageW(&msg, nullptr, WM_HOTKEY, WM_HOTKEY, PM_REMOVE)) {
            if (msg.wParam != kInviteHotKey) continue;
            if (g_lobby.IsValid() && SteamMatchmaking()->GetLobbyOwner(g_lobby) == me) SteamFriends()->ActivateGameOverlayInviteDialog(g_lobby);
            else { g_inviteWhenReady = true; LeaveCurrentLobby(); CreateOwnLobby(); }   // in a friend's lobby: host our own first
        }

        if (tick % 50 == 0) { TailLaunchLog(); UpdatePresence(); }        // about once a second
        if (tick == 1200) Log("Overlay enabled: %s", SteamUtils()->IsOverlayEnabled() ? "yes" : "no");   // ~60 s after start
        Sleep(20);
    }
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(h);
        g_gameThread = GetCurrentThreadId();                       // static import: DllMain runs on the main thread
        // Runs as soon as the loader lock is released, before the game creates its D3D device (overlay needs that order).
        CloseHandle(CreateThread(nullptr, 0, SteamThread, nullptr, 0, nullptr));
    }
    return TRUE;
}
