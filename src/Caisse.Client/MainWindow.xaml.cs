using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Input;

namespace Caisse.Client;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SaleLine> _lines = [];
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5080") };
    private List<Product> _products = [];

    public MainWindow()
    {
        InitializeComponent();
        LinesGrid.ItemsSource = _lines;
        UpdateTotals();
    }

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddLine();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddLine();

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoginButton.IsEnabled = false;
            var response = await _http.PostAsJsonAsync("/api/auth/login",
                new { Username = UsernameBox.Text.Trim(), Password = PasswordBox.Password });
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show("Identifiants invalides.", "Connexion", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var login = await response.Content.ReadFromJsonAsync<LoginResponse>()
                ?? throw new InvalidOperationException("Réponse de connexion invalide.");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            UserText.Text = $"{login.User.DisplayName} - {login.User.Role}";
            AddButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            ValidateButton.IsEnabled = true;
            await LoadProductsAsync();
            CodeBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connexion impossible : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadProductsAsync(); }
        catch (Exception ex) { MessageBox.Show($"Catalogue indisponible : {ex.Message}"); }
    }

    private async Task LoadProductsAsync()
    {
        _products = await _http.GetFromJsonAsync<List<Product>>("/api/products") ?? [];
        UserText.Text = $"{UserText.Text.Split(" - ")[0]} - {_products.Count} article(s)";
    }

    private void AddLine()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0)
            return;
        if (!TryReadDecimal(QuantityBox.Text, out var quantity) || quantity <= 0)
        {
            MessageBox.Show("La quantité doit être supérieure à zéro.");
            return;
        }

        var product = _products.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            MessageBox.Show("Article inconnu. Actualisez le catalogue.");
            return;
        }

        var existing = _lines.FirstOrDefault(x => x.Code.Equals(product.Code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var index = _lines.IndexOf(existing);
            _lines[index] = existing with { Quantity = existing.Quantity + quantity };
        }
        else
        {
            _lines.Add(new SaleLine(product.Code, product.Designation, quantity, product.SalePrice, 0, quantity * product.SalePrice));
        }
        CodeBox.Clear();
        QuantityBox.Text = "1";
        CodeBox.Focus();
        UpdateTotals();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (LinesGrid.SelectedItem is SaleLine line)
        {
            _lines.Remove(line);
            UpdateTotals();
        }
    }

    private void Payment_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReceivedBox is null)
            return;
        var isCash = PaymentBox.SelectedIndex == 0;
        ReceivedBox.IsEnabled = isCash;
        if (!isCash)
            ReceivedBox.Text = TotalValue().ToString(CultureInfo.InvariantCulture);
        UpdateTotals();
    }

    private void Received_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTotals();

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0)
        {
            MessageBox.Show("Le ticket est vide.");
            return;
        }

        var total = TotalValue();
        var received = PaymentBox.SelectedIndex == 0 && TryReadDecimal(ReceivedBox.Text, out var cash) ? cash : total;
        if (received < total)
        {
            MessageBox.Show("Le montant reçu est insuffisant.");
            return;
        }

        try
        {
            ValidateButton.IsEnabled = false;
            var request = new
            {
                PaymentMethod = ((System.Windows.Controls.ComboBoxItem)PaymentBox.SelectedItem).Content.ToString(),
                Subtotal = total,
                Discount = 0m,
                Total = total,
                AmountReceived = received,
                IdempotencyKey = Guid.NewGuid(),
                Lines = _lines.Select(x => new { x.Code, x.Quantity, x.UnitPrice, x.Discount, x.LineTotal }).ToList()
            };
            var response = await _http.PostAsJsonAsync("/api/sales", request);
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show(await response.Content.ReadAsStringAsync(), "Vente refusée");
                return;
            }

            var sale = await response.Content.ReadFromJsonAsync<SaleResponse>();
            MessageBox.Show($"Ticket n° {sale?.TicketNumber} validé.\nMonnaie : {(received - total):N2} DZD",
                "Vente validée", MessageBoxButton.OK, MessageBoxImage.Information);
            _lines.Clear();
            ReceivedBox.Text = "0";
            UpdateTotals();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"La vente n'a pas été envoyée : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ValidateButton.IsEnabled = true;
        }
    }

    private decimal TotalValue() => _lines.Sum(x => x.LineTotal);

    private void UpdateTotals()
    {
        var total = TotalValue();
        TotalText.Text = $"Total : {total:N2} DZD";
        var received = TryReadDecimal(ReceivedBox?.Text, out var value) ? value : 0;
        ChangeText.Text = $"Monnaie : {Math.Max(0, received - total):N2} DZD";
        RemoveButton.IsEnabled = LinesGrid?.SelectedItem is SaleLine;
    }

    private static bool TryReadDecimal(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
        || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private sealed record Product(Guid Id, string Code, string Designation, string? Category,
        decimal PurchasePrice, decimal SalePrice, decimal StockQuantity, decimal LowStockThreshold);
    private sealed record LoginResponse(string Token, LoginUser User);
    private sealed record LoginUser(Guid Id, string Username, string DisplayName, string Role);
    private sealed record SaleResponse(Guid Id, long TicketNumber, decimal Total);
    private sealed record SaleLine(string Code, string Designation, decimal Quantity, decimal UnitPrice,
        decimal Discount, decimal LineTotal);
}
