using System.Collections;
using System.Collections.Specialized;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows a view model collection to the XAML <c>ItemsSource</c> as a plain <see cref="IList"/> that reports changes.
/// </summary>
/// <remarks>
/// <para>
/// Under Native AOT, XAML reaches a list through WinRT interfaces. The CsWinRT source generator writes those interfaces
/// only for partial classes of this project. The <c>ObservableCollection&lt;T&gt;</c> collections of the view models
/// come from Presentation, so they get none. <c>ItemsSource</c> then throws "Value does not fall within the expected
/// range". This partial wrapper gets <c>IBindableVector</c> and <c>INotifyCollectionChanged</c>.
/// </para>
/// <para>
/// XAML can only read the wrapper. Each read and each change event goes to the wrapped collection, and only the view
/// model edits that collection. Set the wrapper with <see cref="Show"/>. It keeps 1 wrapper for each list and detaches
/// the wrapper that it replaces.
/// </para>
/// <para>
/// Pass the collection to <see cref="Show"/> by its concrete type, such as <c>chat.Messages</c>, and not as a generic
/// <c>T</c>. Each change event carries a change list to XAML: a <c>ReadOnlyList</c> or a
/// <c>SingleItemReadOnlyList</c>. The generator writes the vtables for these lists only from a cast in this project.
/// The cast must turn an <c>ObservableCollection&lt;X&gt;</c> of a known <c>X</c> into an <see cref="IList"/>.
/// Without these vtables, no change reaches XAML and the list stays empty.
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
    /// The method does nothing when the control already shows that collection, so the selection and scroll position
    /// stay. A replaced wrapper stops listening to its collection. Thus repeated list swaps leave no old handlers.
    /// </remarks>
    /// <param name="view">The list control.</param>
    /// <param name="items">The <c>ObservableCollection&lt;T&gt;</c> of the view model, or null to show nothing.</param>
    /// <exception cref="ArgumentException"><paramref name="items"/> does not report its changes.</exception>
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

    // Sends each change with this wrapper as the sender, because XAML knows only the wrapper.
    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);

    private void Detach() => _source.CollectionChanged -= OnSourceChanged;
}
