using MdReader.Shell.ViewModels;
using MdReader.Ui.Platform.Windows;

namespace MdReader.Ui.Documents;

internal static partial class DocumentWebViewHostFactory
{
    internal static partial IDocumentWebViewHost Create(DocumentViewServices services) =>
        new EdgeDocumentWebViewHost(services);
}
