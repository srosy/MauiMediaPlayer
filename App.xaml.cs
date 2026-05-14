using MauiMediaPlayer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MauiMediaPlayer;

public partial class App : Application
{
	private readonly IServiceProvider _serviceProvider;

	public App(IServiceProvider serviceProvider)
	{
		_serviceProvider = serviceProvider;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(_serviceProvider.GetRequiredService<MainPage>())
		{
			Title = Branding.FormatWindowTitle(null),
		};

		// Hook the WindowTitleService so PlaybackService -> WindowTitleService ->
		// MAUI Window updates flow without Blazor components needing a MAUI dep.
		// Marshal to main thread because the Title setter touches UI state. The
		// delegate is stored locally so we can detach on window destroy — otherwise
		// a second CreateWindow call (multi-window scenarios, app resume) would
		// stack handlers and we'd push every title update to a stale window object.
		var titleService = _serviceProvider.GetRequiredService<WindowTitleService>();

		void HandleTitleChanged(string title)
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				try
				{
					window.Title = title;
				}
				catch (Exception)
				{
					// Window may have been disposed during shutdown; the next title push
					// would just no-op anyway.
				}
			});
		}

		titleService.TitleChanged += HandleTitleChanged;

		window.Destroying += (_, _) =>
		{
			titleService.TitleChanged -= HandleTitleChanged;
		};

		return window;
	}
}
