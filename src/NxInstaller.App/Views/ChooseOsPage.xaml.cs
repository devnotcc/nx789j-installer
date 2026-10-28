using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NxInstaller.App.Services;

namespace NxInstaller.App.Views;

public sealed partial class ChooseOsPage : Page, IWizardPage
{
    readonly AppServices _app = AppServices.Current;
    readonly Dictionary<string, Button> _cards = [];
    readonly List<RadioButton> _variantButtons = [];

    public ChooseOsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    public string PrimaryText => "Next";
    public bool CanContinue => _app.Selected is { Available: true };
    public event EventHandler? StateChanged;

    async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        RefreshLink.IsEnabled = false;
        SourceText.Text = $"Packages from {_app.Source.Name}";
        var error = await _app.RefreshPackagesAsync();
        SourceError.IsOpen = error is not null;
        SourceError.Title = "Couldn't load the list of packages";
        SourceError.Message = error is null ? "" : $"{error} Check the internet connection, or pick a local folder on the next page.";
        LoadingRing.IsActive = false;
        RefreshLink.IsEnabled = true;
        if (_app.Selected is { Available: false }) _app.Selected = null;
        Build();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    void Build()
    {
        Cards.Children.Clear();
        _cards.Clear();
        _variantButtons.Clear();
        // Items that share a title (the two LineageOS variants) become one card with a choice inside.
        foreach (var group in _app.Catalog.GroupBy(c => c.Title))
        {
            var items = group.ToList();
            var first = items[0];
            var enabled = items.Any(i => i.Available);

            var title = new TextBlock { Text = first.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            var status = new TextBlock
            {
                Text = items.Count > 1 && items.All(i => i.ComingSoon) ? "Coming soon" : (items.FirstOrDefault(i => i.Available) ?? first).Status,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.7,
            };
            var text = new StackPanel { Spacing = 4 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(title);
            if (!enabled) header.Children.Add(Badge(status.Text));
            text.Children.Add(header);
            text.Children.Add(new TextBlock { Text = first.Subtitle, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            if (enabled) text.Children.Add(status);

            if (items.Count > 1)
            {
                var variants = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
                foreach (var v in items)
                {
                    var rb = new RadioButton { Content = v.Variant, GroupName = "variant-" + first.Title, IsEnabled = v.Available, Tag = v, MinWidth = 0, Margin = new Thickness(0, 0, 16, 0) };
                    rb.Checked += (_, _) => Select(v);
                    if (_app.Selected == v) rb.IsChecked = true;
                    _variantButtons.Add(rb);
                    variants.Children.Add(rb);
                }
                text.Children.Add(variants);
            }

            var icon = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top,
                Background = (Brush)Application.Current.Resources[enabled ? "AccentFillColorDefaultBrush" : "ControlFillColorDisabledBrush"],
                Child = new FontIcon { Glyph = first.Glyph, FontSize = 18, Foreground = (Brush)Application.Current.Resources[enabled ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorDisabledBrush"] },
            };

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(text, 1);
            grid.Children.Add(icon);
            grid.Children.Add(text);

            var card = new Button
            {
                Style = (Style)Application.Current.Resources["CardButton"],
                Content = grid,
                Tag = items,
                IsEnabled = enabled,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, first.Title);
            card.Click += Card_Click;
            Cards.Children.Add(card);
            foreach (var i in items) _cards[i.Id] = card;
        }
        UpdateSelection();
    }

    static Border Badge(string text) => new()
    {
        CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
        Child = new TextBlock { Text = text, FontSize = 11 },
    };

    void Card_Click(object sender, RoutedEventArgs e)
    {
        var items = (List<CatalogItem>)((Button)sender).Tag;
        // For a card with variants, a click on the card picks the first available variant.
        if (items.Contains(_app.Selected!)) return;
        Select(items.First(i => i.Available));
    }

    void Select(CatalogItem item)
    {
        _app.Selected = item;
        foreach (var rb in _variantButtons) rb.IsChecked = ReferenceEquals(rb.Tag, item);
        UpdateSelection();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void UpdateSelection()
    {
        foreach (var (id, card) in _cards)
        {
            var on = _app.Selected?.Id == id;
            if (!on && _app.Selected is not null && _cards.GetValueOrDefault(_app.Selected.Id) == card) continue;
            card.BorderBrush = (Brush)Application.Current.Resources[on ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
            card.BorderThickness = new Thickness(on ? 2 : 1);
        }
    }
}
