using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A view model collection as XAML's <c>ItemsSource</c> sees it: a plain <see cref="IList"/> that reports changes.
/// </summary>
/// <remarks>
/// <para>
/// Native AOT: XAML reaches a list through WinRT interfaces, and CsWinRT's source generator only writes those for
/// partial classes of this project. The view models' <c>ObservableCollection&lt;T&gt;</c>s come from Presentation, so
/// they get none and <c>ItemsSource</c> throws "Value does not fall within the expected range". This partial wrapper
/// gets them (<c>IBindableVector</c> and <c>INotifyCollectionChanged</c>).
/// </para>
/// <para>
/// Read-only for XAML: every read and change event goes to the wrapped collection, which the view model alone edits.
/// Set it with <see cref="Show"/>, which keeps one wrapper per list and unhooks the one it replaces.
/// </para>
/// <para>
/// Pass the collection to <see cref="Show"/> by its concrete type (<c>chat.Messages</c>, not a generic <c>T</c>): the
/// generator only writes the vtables for the collection's own change lists (<c>ReadOnlyList</c>,
/// <c>SingleItemReadOnlyList</c>, which every change event carries to XAML) when it sees an
/// <c>ObservableCollection&lt;X&gt;</c> of a known <c>X</c> turned into an <see cref="IList"/> in this project. Without
/// them every change fails to reach XAML and the list stays empty.
/// </para>
/// </remarks>
internal sealed partial class BindableList : IList, INotifyCollectionChanged
{
    private readonly IList _list;
    private readonly INotifyCollectionChanged _source;

    private BindableList(IList list, INotifyCollectionChanged source)
    {
        _list                      = list;
        _source                    = source;
        _source.CollectionChanged += OnSourceChanged;
    }

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

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

    /// <summary>
    /// Shows a view model collection in a list control, wrapped for XAML.
    /// </summary>
    /// <remarks>
    /// Does nothing when the control already shows that collection, so its selection and scroll position stay. A
    /// wrapper it replaces stops listening to its collection, so swapping lists back and forth leaves no handlers behind.
    /// </remarks>
    /// <param name="view">The list control.</param>
    /// <param name="items">The view model's <c>ObservableCollection&lt;T&gt;</c>, or null to show nothing.</param>
    /// <exception cref="ArgumentException"><paramref name="items"/> doesn't report its changes.</exception>
    public static void Show(ItemsControl view, IList? items)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (items is not null and not INotifyCollectionChanged)
        {
            throw new ArgumentException("The list must report its changes.", nameof(items));
        }

        BindableList? shown = view.ItemsSource as BindableList;
        if (shown is not null && ReferenceEquals(shown._list, items))
        {
            return;
        }

        shown?.Detach();
        view.ItemsSource = items is null ? null : new BindableList(items, (INotifyCollectionChanged)items);
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

    // Passes On A Change With This Wrapper As The Sender (XAML Knows Only The Wrapper)
    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);

    private void Detach() => _source.CollectionChanged -= OnSourceChanged;
}
