// language: C#, file: RemoteChangedException.cs, target: .NET 9
namespace NovaDownload.DownloadEngine.Http;

/// <summary>
/// The bytes already on disk no longer describe the object the server is serving. Continuing would
/// splice two different versions into one file, so the transfer stops and the caller decides
/// between restarting and dropping the row.
/// </summary>
public sealed class RemoteChangedException : Exception
{
    public RemoteChangedException(string message) : base(message)
    {
    }
}
