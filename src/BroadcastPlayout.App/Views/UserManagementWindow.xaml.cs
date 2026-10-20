using System.Windows;
using System.Windows.Controls;
using BroadcastPlayout.Services;
namespace BroadcastPlayout.Views;
public partial class UserManagementWindow:Window
{
 private readonly SecurityStore _store=new(); private KashtrixUser? _selected;
 public UserManagementWindow(){InitializeComponent();Loaded+=(_,_)=>{if(!AppSession.IsAdmin){MessageBox.Show("Administrator role required.","Kashtrix Security",MessageBoxButton.OK,MessageBoxImage.Warning);Close();return;}Refresh();};}
 private void Refresh(){UsersGrid.ItemsSource=_store.ListUsers();ApiGrid.ItemsSource=_store.ListApiCredentials();}
 private static string Combo(ComboBox box,string fallback)=>((box.SelectedItem as ComboBoxItem)?.Content?.ToString()??fallback).Trim();
 private void UsersGrid_SelectionChanged(object sender,SelectionChangedEventArgs e){_selected=UsersGrid.SelectedItem as KashtrixUser;if(_selected is null)return;UsernameBox.Text=_selected.Username;DisplayNameBox.Text=_selected.DisplayName;EnabledCheck.IsChecked=_selected.Enabled;foreach(var item in RoleBox.Items.OfType<ComboBoxItem>())if(string.Equals(item.Content?.ToString(),_selected.Role,StringComparison.OrdinalIgnoreCase)){RoleBox.SelectedItem=item;break;}}
 private void CreateUser_Click(object sender,RoutedEventArgs e){try{_store.CreateUser(UsernameBox.Text,DisplayNameBox.Text,Combo(RoleBox,"OPERATOR"),PasswordBox.Password);PasswordBox.Clear();Refresh();MessageBox.Show("User created.","Kashtrix Security");}catch(Exception ex){MessageBox.Show(ex.Message,"Kashtrix Security",MessageBoxButton.OK,MessageBoxImage.Warning);}}
 private void SaveUser_Click(object sender,RoutedEventArgs e){if(_selected is null)return;try{_store.SetUserState(_selected.Username,Combo(RoleBox,_selected.Role),EnabledCheck.IsChecked==true);Refresh();}catch(Exception ex){MessageBox.Show(ex.Message,"Kashtrix Security");}}
 private void ResetPassword_Click(object sender,RoutedEventArgs e){if(_selected is null)return;try{_store.ResetPassword(_selected.Username,PasswordBox.Password);PasswordBox.Clear();MessageBox.Show("Password hash replaced.","Kashtrix Security");}catch(Exception ex){MessageBox.Show(ex.Message,"Kashtrix Security");}}
 private void CreateApi_Click(object sender,RoutedEventArgs e){try{var created=_store.CreateApiCredential(ApiNameBox.Text,ApiUserBox.Text,ScopesBox.Text);TokenText.Text="COPY NOW — token will not be shown again:\n"+created.Token;Refresh();}catch(Exception ex){MessageBox.Show(ex.Message,"Kashtrix API");}}
 private void RevokeApi_Click(object sender,RoutedEventArgs e){if(ApiGrid.SelectedItem is not KashtrixApiCredential row)return;_store.RevokeApiCredential(row.Id);TokenText.Text="Selected credential revoked.";Refresh();}
}
