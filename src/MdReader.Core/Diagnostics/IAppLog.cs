namespace MdReader.Core.Diagnostics;

public enum AppLogLevel { Debug, Info, Warning, Error }

public interface IAppLog { void Write(AppLogLevel level, string category, string message, Exception? exception = null); }

public sealed class NullAppLog : IAppLog
{
    public static NullAppLog Instance { get; } = new();
    public void Write(AppLogLevel level, string category, string message, Exception? exception = null) { }
}
