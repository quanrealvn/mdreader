namespace MdReader.Core.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string AppHost = "app.mdreader.example";
    public const string DocHost = "doc.mdreader.example";
    public const string AppOrigin = "https://app.mdreader.example";
    public const string DocBaseUrl = "https://doc.mdreader.example/";
    public const string PageUrl = "https://app.mdreader.example/index.html";   // + "?theme=light|dark"
    public const int MaxPartChars = 1_000_000;
    public const int MaxIncomingMessageChars = 8_000_000;
}
