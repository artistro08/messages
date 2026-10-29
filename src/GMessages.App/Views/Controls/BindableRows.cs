using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GMessages.Presentation.Main;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A conversation row list as XAML's <c>ItemsSource</c> sees it: a plain <see cref="IList"/> that reports changes.
/// </summary>
/// <remarks>
/// <para>
/// Native AOT: XAML reaches a list through WinRT interfaces, and CsWinRT's source generator only writes those for
/// partial classes of this project. The view model's <c>ObservableCollection&lt;ConversationRowViewModel&gt;</c> comes
/// from Presentation, so it gets none and <c>ItemsSource</c> throws "Value does not fall within the expected range".
/// This partial wrapper gets them (<c>IBindableVector</c> and <c>INotifyCollectionChanged</c>).
/// </para>
/// <para>
/// Read-only for XAML: every read and change event goes to the wrapped collection, which the view model alone edits.
/// </para>
/// </remarks>
internal sealed partial class BindableRows : IList, INotifyCollectionChanged
{
    private readonly IList _list;

    /// <summary>
    /// Wraps the rows and passes on their changes, with this wrapper as the sender (XAML knows only the wrapper).
    /// </summary>
    /// <remarks>
    /// The rows belong to the main window's view model, so the subscription goes with the window and needs no
    /// unhooking (a wrapper replaced on a list swap just stays subscribed until then).
    /// </remarks>
    /// <param name="rows">The view model's rows.</param>
    public BindableRows(ObservableCollection<ConversationRowViewModel> rows)
    {
        Rows                    = rows;
        _list                   = rows;
        rows.CollectionChanged += (_, e) => CollectionChanged?.Invoke(this, e);
    }

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Gets the wrapped rows.</summary>
    public ObservableCollection<ConversationRowViewModel> Rows { get; }

    /// <inheritdoc />
    public int Count => _list.Count;

    /// <inheritdoc />
    public bool IsFixedSize => false;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsSynchronized => false;

    /// <inheritdoc />
    public object SyncRoot => _list.SyncRoot;

    /// <inheritdoc />
    public object? this[int index]
    {
        get => _list[index];
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public bool Contains(object? value) => _list.Contains(value);

    /// <inheritdoc />
    public int IndexOf(object? value) => _list.IndexOf(value);

    /// <inheritdoc />
    public void CopyTo(Array array, int index) => _list.CopyTo(array, index);

    /// <inheritdoc />
    public IEnumerator GetEnumerator() => _list.GetEnumerator();

    /// <inheritdoc />
    public int Add(object? value) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Clear() => throw new NotSupportedException();

    /// <inheritdoc />
    public void Insert(int index, object? value) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Remove(object? value) => throw new NotSupportedException();

    /// <inheritdoc />
    public void RemoveAt(int index) => throw new NotSupportedException();
}
