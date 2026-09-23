using MdReader.Shell.ViewModels;
using MdReader.Ui.Platform.Mac;

namespace MdReader.Ui.Documents;

internal static partial class DocumentWebViewHostFactory
{
    internal static partial IDocumentWebViewHost Create(DocumentViewServices services) =>
        new WkDocumentWebViewHost(services);
}
