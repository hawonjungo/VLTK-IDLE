using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VLMulti.Core.Accounts;
using VLMulti.Core.Settings;

namespace VLMulti.Desktop.Dialogs;

public partial class AccountManagerWindow : Window
{
    private readonly CredentialStore _store;
    private readonly List<Row> _rows = new();

    // Các acc đã đổi mật khẩu/tài khoản → MainWindow cho tự đăng nhập thử lại từ đầu
    public List<string> ChangedAccounts { get; } = new();

    public AccountManagerWindow(IEnumerable<AccountSlot> accounts, CredentialStore store)
    {
        InitializeComponent();
        _store = store;
        foreach (var acc in accounts)
            AddRow(acc);
    }

    private void AddRow(AccountSlot acc)
    {
        int r = Rows.RowDefinitions.Count;
        Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var row = new Row
        {
            Account = acc,
            User = new TextBox { Text = _store.UserOf(acc.Id) ?? "" },
            Password = new PasswordBox(),
            AutoLogin = new CheckBox { IsChecked = acc.AutoLogin, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            Saved = new TextBlock { VerticalAlignment = VerticalAlignment.Center },
        };
        StyleInput(row.User);
        StyleInput(row.Password);
        UpdateSaved(row);

        var forget = new Button { Content = "Xoá mật khẩu", Style = (Style)FindResource("BarButton"), Margin = new Thickness(6, 0, 0, 0) };
        forget.Click += (_, _) =>
        {
            _store.Remove(acc.Id);
            row.Password.Clear();
            ChangedAccounts.Add(acc.Id);
            UpdateSaved(row);
        };

        Place(new TextBlock { Text = acc.Id, VerticalAlignment = VerticalAlignment.Center }, r, 0);
        Place(new TextBlock { Text = acc.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, r, 1);
        Place(row.User, r, 2);
        var pwCell = new StackPanel();
        pwCell.Children.Add(row.Password);
        pwCell.Children.Add(row.Saved);
        Place(pwCell, r, 3);
        Place(row.AutoLogin, r, 4);
        Place(forget, r, 5);
        _rows.Add(row);
    }

    private void Place(UIElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        if (el is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, 3, 10, 3);
        Rows.Children.Add(el);
    }

    private void UpdateSaved(Row row)
    {
        bool saved = _store.Has(row.Account.Id);
        row.Saved.Text = saved ? "✓ đã lưu" : "chưa lưu";
        row.Saved.FontSize = 11;
        row.Saved.Foreground = saved ? Brushes.MediumSeaGreen : (Brush)FindResource("TextDim");
    }

    private static void StyleInput(Control c)
    {
        c.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x37));
        c.Foreground = Brushes.White;
        c.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4F));
        c.Padding = new Thickness(4, 2, 4, 2);
        if (c is TextBox tb) tb.CaretBrush = Brushes.White;
        if (c is PasswordBox pb) pb.CaretBrush = Brushes.White;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            var id = row.Account.Id;
            var user = row.User.Text.Trim();
            var password = row.Password.Password;

            row.Account.AutoLogin = row.AutoLogin.IsChecked == true;

            if (password.Length > 0)
            {
                if (user.Length == 0)
                {
                    MessageBox.Show(this, $"{id}: hãy nhập tên tài khoản kèm mật khẩu.", "Quản lý acc", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _store.Set(id, user, password);
                ChangedAccounts.Add(id);
            }
            else if (_store.Get(id) is { } old && old.User != user && user.Length > 0)
            {
                // Chỉ đổi tên tài khoản, giữ mật khẩu cũ
                _store.Set(id, user, old.Password);
                ChangedAccounts.Add(id);
            }
        }
        DialogResult = true;
    }

    private sealed class Row
    {
        public required AccountSlot Account { get; init; }
        public required TextBox User { get; init; }
        public required PasswordBox Password { get; init; }
        public required CheckBox AutoLogin { get; init; }
        public required TextBlock Saved { get; init; }
    }
}
