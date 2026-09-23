using System.Collections.ObjectModel;
using MdReader.Shell.ViewModels;

namespace MdReader.Shell.Services;

// Implemented by MainViewModel (WP5); used by DocumentOpener/DocumentHost (WP6).
public interface ITabHost
{
    ReadOnlyObservableCollection<IDocumentTab> Tabs { get; }
    IDocumentTab? ActiveTab { get; }
    void Add(IDocumentTab tab, bool activate);                 // appends; activates if requested or if it is the only tab
    void Activate(IDocumentTab tab);
    void Close(IDocumentTab tab);                              // removes, calls tab.Dispose(), activates a neighbor
}
