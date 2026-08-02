// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win;

public partial class StorageCredentialsWindow : Window
{
    public StorageCredentialsWindow(EdgeStorageCredentials credentials)
    {
        InitializeComponent();
        AddressTextBox.Text = string.Join(Environment.NewLine, credentials.SharePaths);
        UsernameTextBox.Text = credentials.Username;
        PasswordTextBox.Text = credentials.Password;
    }

    private void OnCopyAddressClick(object sender, RoutedEventArgs e) => Copy(AddressTextBox.Text);

    private void OnCopyUsernameClick(object sender, RoutedEventArgs e) => Copy(UsernameTextBox.Text);

    private void OnCopyPasswordClick(object sender, RoutedEventArgs e) => Copy(PasswordTextBox.Text);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        PasswordTextBox.Clear();
        base.OnClosed(e);
    }

    private static void Copy(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Clipboard.SetText(value);
        }
    }
}
