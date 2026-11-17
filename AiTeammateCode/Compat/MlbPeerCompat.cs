using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Interop shim for Multiplayer Limit Break (STS2-MultiplayerLimitBreak).
///
/// The AI teammate is a synthetic lobby player with a reserved NetId and no transport connection, but
/// it still lives in <see cref="StartRunLobby.Players"/>. MLB learns peer capabilities only from real
/// lobby messages (InitialGameInfo / ClientLobbyJoinRequest / PlayerJoined message tails), so every
/// roster entry without a recorded capability is treated as an incompatible client: the lobby header
/// flips to "disabled by incompatible clients", the AI nameplate gets a warning marker, and once the
/// roster reaches four entries the next human is rejected with <see cref="NetError.ModMismatch"/> —
/// the vanilla "mods mismatch" popup, even though both sides run identical mods.
///
/// The AI needs no wire protocol (it never sends or receives anything), so the host simply records the
/// local capability for the AI's NetId in MLB's own protocol state. Every MLB consumer (status panel,
/// join gate, run-start snapshot) then reads the same picture without patching MLB itself.
/// </summary>
internal static class MlbPeerCompat
{
    private const string ModId = "STS2-MultiplayerLimitBreak";
    private const string RegistryTypeName =
        "STS2MultiplayerLimitBreak.Network.Protocol.MlbLobbyProtocolRegistry";
    private const string StateTypeName =
        "STS2MultiplayerLimitBreak.Network.Protocol.MlbLobbyProtocolState";
    private const string CapabilityTypeName =
        "STS2MultiplayerLimitBreak.Network.Protocol.MlbPeerCapability";

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static readonly object Gate = new();

    private static bool _resolved;
    private static bool _reportedUnavailable;
    private static bool _reportedRegistered;

    private static MethodInfo? _getOrCreate;
    private static MethodInfo? _tryGet;
    private static MethodInfo? _recordCapability;
    private static MethodInfo? _removePeer;
    private static PropertyInfo? _localCapability;
    private static Type? _capabilityType;

    /// <summary>
    /// Marks the AI's NetId as a protocol-capable member of the host lobby. Idempotent; safe to call
    /// when MLB is absent (no-op) or when the AI is already registered (MLB ignores no-op writes).
    /// </summary>
    public static void RegisterAiPeer(StartRunLobby lobby, ulong aiNetId)
    {
        if (!IsHostLobby(lobby) || !EnsureResolved())
            return;

        try
        {
            var state = _getOrCreate!.Invoke(null, [lobby])
                        ?? throw new InvalidOperationException("MLB returned no protocol state.");
            var capability = _localCapability!.GetValue(null)
                             ?? throw new InvalidOperationException("MLB exposed no local capability.");
            _recordCapability!.Invoke(state, [aiNetId, capability]);

            if (!_reportedRegistered)
            {
                _reportedRegistered = true;
                MainFile.Logger.Info(
                    $"[MLB] 已为 AI 队友登记多人上限解限能力（NetId=0x{aiNetId:X}），房间不再把它当作不兼容客户端");
            }
        }
        catch (Exception ex)
        {
            ReportUnavailable($"登记 AI 扩容能力失败（Multiplayer Limit Break 可能已改版）: {ex.Message}");
        }
    }

    /// <summary>Drops the AI's capability entry when the AI leaves the lobby.</summary>
    public static void UnregisterAiPeer(StartRunLobby lobby, ulong aiNetId)
    {
        if (!IsHostLobby(lobby) || !EnsureResolved())
            return;

        try
        {
            var args = new object?[] { lobby, null };
            if (_tryGet!.Invoke(null, args) is not true || args[1] is not { } state)
                return;

            _removePeer!.Invoke(state, [aiNetId]);
        }
        catch (Exception ex)
        {
            ReportUnavailable($"[MLB] 清理 AI 扩容能力失败: {ex.Message}");
        }
    }

    private static bool IsHostLobby(StartRunLobby? lobby)
    {
        try
        {
            return lobby?.NetService.Type == NetGameType.Host;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureResolved()
    {
        if (Volatile.Read(ref _resolved))
            return true;

        lock (Gate)
        {
            if (_resolved)
                return true;

            var registryType = FindType(RegistryTypeName);
            if (registryType == null)
            {
                ReportUnavailable("未检测到 Multiplayer Limit Break");
                return false;
            }

            var assembly = registryType.Assembly;
            var stateType = FindType(StateTypeName, assembly);
            _capabilityType = FindType(CapabilityTypeName, assembly);
            if (stateType == null || _capabilityType == null)
            {
                ReportUnavailable("Multiplayer Limit Break 缺少预期的协议类型");
                return false;
            }

            _getOrCreate = registryType.GetMethod(
                "GetOrCreate", Declared, null, [typeof(StartRunLobby)], null);
            _tryGet = registryType.GetMethod("TryGet", Declared);
            _recordCapability = stateType.GetMethod("RecordCapability", Declared);
            _removePeer = stateType.GetMethod("RemovePeer", Declared);
            _localCapability = _capabilityType.GetProperty("Local", Declared);

            if (_getOrCreate == null ||
                _tryGet == null || _tryGet.GetParameters() is not
                    [{ ParameterType: var first }, _, ..] ||
                first != typeof(StartRunLobby) ||
                _recordCapability == null || _recordCapability.GetParameters().Length != 2 ||
                _removePeer == null || _removePeer.GetParameters().Length != 1 ||
                _localCapability == null)
            {
                ReportUnavailable("Multiplayer Limit Break 的协议接口与预期不符");
                return false;
            }

            _resolved = true;
            MainFile.Logger.Info(
                $"[MLB] 检测到 Multiplayer Limit Break {assembly.GetName().Version}，AI 队友将登记扩容能力");
            return true;
        }
    }

    private static Type? FindType(string typeName, Assembly? assembly = null)
    {
        if (assembly != null)
        {
            try
            {
                return assembly.GetType(typeName, throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = candidate.GetType(typeName, throwOnError: false);
                if (type != null)
                    return type;
            }
            catch
            {
                // Dynamic / partially loaded assemblies can throw here; just skip them.
            }
        }

        return null;
    }

    private static void ReportUnavailable(string reason)
    {
        if (_reportedUnavailable)
            return;

        _reportedUnavailable = true;

        // Silent when the mod is not installed at all: this shim exists only for MLB rooms.
        if (IsModInstalled())
            MainFile.Logger.Warn($"[MLB] {reason}，AI 队友不会登记扩容能力（大厅可能仍显示不兼容客户端）");
    }

    private static bool IsModInstalled()
    {
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (string.Equals(mod.manifest?.id, ModId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Mod list unavailable during early startup; treat as not installed.
        }

        return false;
    }
}
