using System.Text;
using DeadCellsMultiplayerMod.PortableCore;
using Newtonsoft.Json;

namespace DeadCellsMultiplayerMod;

/// <summary>
/// Temporary adapter between the portable launch contracts and the existing line protocol.
/// Payloads are typed JSON wrapped in Base64, so delimiters and future fields cannot corrupt a line.
/// </summary>
internal static class RunLaunchWireCodec
{
    internal const string CommitTag = "RUNCOMMIT";
    internal const string AckTag = "RUNACK";
    internal const string ExecuteTag = "RUNEXEC";
    internal const string QueuedTag = "RUNQUEUED";
    internal const string ReadyTag = "RUNREADY";
    internal const string CancelTag = "RUNCANCEL";

    private const int MaxEncodedPayloadLength = 64 * 1024;

    internal static string BuildCommitLine(RunLaunchDescriptor descriptor) =>
        BuildLine(CommitTag, descriptor);

    internal static string BuildAckLine(RunLaunchAck ack) =>
        BuildLine(AckTag, ack);

    internal static string BuildExecuteLine(RunLaunchExecute execute) =>
        BuildLine(ExecuteTag, execute);

    internal static string BuildQueuedLine(RunLaunchQueued queued) =>
        BuildLine(QueuedTag, queued);

    internal static string BuildReadyLine(RunLevelReady ready) =>
        BuildLine(ReadyTag, ready);

    internal static string BuildCancelLine(RunLaunchCancel cancel) =>
        BuildLine(CancelTag, cancel);

    internal static string EncodeCommitPayload(RunLaunchDescriptor descriptor) => Encode(descriptor);
    internal static string EncodeExecutePayload(RunLaunchExecute execute) => Encode(execute);
    internal static string EncodeReadyPayload(RunLevelReady ready) => Encode(ready);

    internal static bool TryDecodeCommit(string payload, out RunLaunchDescriptor? descriptor, out string error) =>
        TryDecode(payload, out descriptor, out error);

    internal static bool TryDecodeAck(string payload, out RunLaunchAck? ack, out string error) =>
        TryDecode(payload, out ack, out error);

    internal static bool TryDecodeExecute(string payload, out RunLaunchExecute? execute, out string error) =>
        TryDecode(payload, out execute, out error);

    internal static bool TryDecodeQueued(string payload, out RunLaunchQueued? queued, out string error) =>
        TryDecode(payload, out queued, out error);

    internal static bool TryDecodeReady(string payload, out RunLevelReady? ready, out string error) =>
        TryDecode(payload, out ready, out error);

    internal static bool TryDecodeCancel(string payload, out RunLaunchCancel? cancel, out string error) =>
        TryDecode(payload, out cancel, out error);

    private static string BuildLine<T>(string tag, T message) where T : class =>
        $"{tag}|{Encode(message)}";

    private static string Encode<T>(T message) where T : class
    {
        var json = JsonConvert.SerializeObject(message, Formatting.None);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static bool TryDecode<T>(string payload, out T? message, out string error) where T : class
    {
        message = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "payload is empty";
            return false;
        }

        var trimmed = payload.Trim();
        if (trimmed.Length > MaxEncodedPayloadLength)
        {
            error = $"payload exceeds {MaxEncodedPayloadLength} encoded characters";
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            message = JsonConvert.DeserializeObject<T>(json);
            if (message == null)
            {
                error = "decoded message is null";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
