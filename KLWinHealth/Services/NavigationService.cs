using CommunityToolkit.Mvvm.ComponentModel;
using KLWinHealth.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>Single-level navigation: Home ⇄ module page. Page view models are singletons and keep their state.</summary>
public partial class NavigationService : ObservableObject
{
    private readonly IServiceProvider _services;

    [ObservableProperty] private PageViewModel? _currentPage;

    public NavigationService(IServiceProvider services) => _services = services;

    public void GoHome() => NavigateTo<HomeViewModel>();

    public void NavigateTo<T>() where T : PageViewModel => NavigateTo(typeof(T));

    public async void NavigateTo(Type pageType)
    {
        try
        {
            var page = (PageViewModel)_services.GetRequiredService(pageType);
            if (ReferenceEquals(page, CurrentPage)) return;
            CurrentPage?.OnNavigatedFrom();
            CurrentPage = page;
            Log.Debug("Navigated to {Page}", pageType.Name);
            await page.OnNavigatedToAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Navigation to {Page} failed", pageType.Name);
        }
    }
}
