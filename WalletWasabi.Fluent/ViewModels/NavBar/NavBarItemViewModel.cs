using ReactiveUI;
using System.Windows.Input;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.NavBar;

public enum NavBarItemSelectionMode
{
	Selected = 0,
	Button = 1,
	Toggle = 2
}

public abstract class NavBarItemViewModel : RoutableViewModel
{
	private bool _isSelected;

	protected NavBarItemViewModel(NavigationMode defaultNavigationMode = NavigationMode.Clear)
	{
		SelectionMode = NavBarItemSelectionMode.Selected;

		OpenCommand = ReactiveCommand.Create(
			() => OnOpen(defaultNavigationMode));
	}

	public NavBarItemSelectionMode SelectionMode { get; protected init; }

	public bool IsSelectable => SelectionMode == NavBarItemSelectionMode.Selected;

	public bool IsSelected
	{
		get => _isSelected;
		set
		{
			switch (SelectionMode)
			{
				case NavBarItemSelectionMode.Selected:
					this.RaiseAndSetIfChanged(ref _isSelected, value);
					break;
				case NavBarItemSelectionMode.Button:
				case NavBarItemSelectionMode.Toggle:
					break;
			}
		}
	}

	public ICommand OpenCommand { get; protected set; }

	private void OnOpen(NavigationMode defaultNavigationMode)
	{
		if (SelectionMode == NavBarItemSelectionMode.Toggle)
		{
			Toggle();
		}
		else
		{
			Navigate().To(this, defaultNavigationMode);
		}
	}

	public virtual void Toggle()
	{
	}
}
