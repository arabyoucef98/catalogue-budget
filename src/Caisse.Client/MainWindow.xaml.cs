using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Input;

namespace Caisse.Client;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SaleLine> _lines = [];
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5080") };
    public MainWindow()
    {
        InitializeComponent();
        LinesGrid.ItemsSource = _lines;
    }
    private void CodeBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddLine(); }
    private void Add_Click(object sender, RoutedEventArgs e) => AddLine();
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/login", new { Username = UsernameBox.Text.Trim(), Password = PasswordBox.Password });
        if (!response.IsSuccessStatusCode) { MessageBox.Show("Identifiants invalides."); return; }
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Token);
        AddButton.IsEnabled = true;
        ValidateButton.IsEnabled = true;
        MessageBox.Show($"Bienvenue {login.User.DisplayName}.");
    }
    private async void AddLine()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0 || !decimal.TryParse(QuantityBox.Text, out var quantity) || quantity <= 0) return;
        try
        {
            var products = await _http.GetFromJsonAsync<List<Product>>("/api/products");
            var product = products?.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (product is null) { MessageBox.Show("Article inconnu."); return; }
            _lines.Add(new SaleLine(product.Code, product.Designation, quantity, product.SalePrice, 0, quantity * product.SalePrice));
            TotalText.Text = $"Total : {_lines.Sum(x => x.LineTotal):N2} DZD";
            CodeBox.Clear(); CodeBox.Focus();
        }
        catch (Exception ex) { MessageBox.Show($"Serveur indisponible : {ex.Message}"); }
    }
    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0) return;
        var total = _lines.Sum(x => x.LineTotal);
        var request = new { PaymentMethod = "Espèces", Subtotal = total, Discount = 0m, Total = total, AmountReceived = total, IdempotencyKey = Guid.NewGuid(), Lines = _lines.Select(x => new { x.Code, x.Quantity, UnitPrice = x.UnitPrice, x.Discount, x.LineTotal }).ToList() };
        var response = await _http.PostAsJsonAsync("/api/sales", request);
        if (!response.IsSuccessStatusCode) { MessageBox.Show(await response.Content.ReadAsStringAsync()); return; }
        MessageBox.Show("Vente validée.");
        _lines.Clear(); TotalText.Text = "Total : 0,00 DZD";
    }
    private sealed record Product(Guid Id, string Code, string Designation, string? Category, decimal PurchasePrice, decimal SalePrice, decimal StockQuantity, decimal LowStockThreshold);
    private sealed record LoginResponse(string Token, LoginUser User);
    private sealed record LoginUser(Guid Id, string Username, string DisplayName, string Role);
    private sealed record SaleLine(string Code, string Designation, decimal Quantity, decimal UnitPrice, decimal Discount, decimal LineTotal);
}
