using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Host -> clients replication for everything the AI teammate decides or owns. Three payload kinds
/// share one message so the game only has to register a single mod message type:
///
/// <list type="bullet">
/// <item><see cref="AiSyncBus.KindCombatPlan"/> — a combat decision plan (JSON of the validated actions).</item>
/// <item><see cref="AiSyncBus.KindChoice"/> — one out-of-combat choice index (card reward / event / rest).</item>
/// <item><see cref="AiSyncBus.KindState"/> — the host's authoritative <see cref="SerializablePlayer"/> snapshot of the AI player.</item>
/// </list>
/// </summary>
public struct AiSyncMessage : INetMessage
{
    /// <summary>One of the <see cref="AiSyncBus"/> kind constants.</summary>
    public byte kind;

    /// <summary>Deterministic context key, e.g. <c>combat:12:2:1</c> or <c>event:NEOW:3</c>.</summary>
    public string key;

    /// <summary>JSON payload (combat plan / out-of-combat choice). Empty for state snapshots.</summary>
    public string json;

    /// <summary>Authoritative AI player snapshot; only set for state messages.</summary>
    public SerializablePlayer player;

    public bool hasPlayer;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(kind);
        writer.WriteString(key ?? string.Empty);
        writer.WriteString(json ?? string.Empty);
        writer.WriteBool(hasPlayer);
        if (hasPlayer)
        {
            writer.Write(player);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        kind = reader.ReadByte();
        key = reader.ReadString();
        json = reader.ReadString();
        hasPlayer = reader.ReadBool();
        if (hasPlayer)
        {
            player = reader.Read<SerializablePlayer>();
        }
    }
}
