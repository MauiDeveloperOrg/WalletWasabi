using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Linq;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History.Details;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Home.History.HistoryItems;

public class CoinJoinsHistoryItemViewModel : HistoryItemViewModelBase
{
	private readonly WalletViewModel _walletVm;

	public CoinJoinsHistoryItemViewModel(
		int orderIndex,
		TransactionSummary firstItem,
		WalletViewModel walletVm)
		: base(orderIndex, firstItem)
	{
		_walletVm = walletVm;

		CoinJoinTransactions = new List<TransactionSummary>();
		IsCoinJoin = true;
		IsCoinJoinGroup = true;

		ShowDetailsCommand = ReactiveCommand.Create(() =>
			RoutableViewModel.Navigate(NavigationTarget.DialogScreen).To(
				new CoinJoinsDetailsViewModel(this, walletVm.UiTriggers.TransactionsUpdateTrigger)));

		Add(firstItem);
	}

	public List<TransactionSummary> CoinJoinTransactions { get; private set; }

	protected override ObservableCollection<HistoryItemViewModelBase> LoadChildren()
	{
		var result = new ObservableCollection<HistoryItemViewModelBase>();

		var balance = Balance ?? Money.Zero;

		for (var i = 0; i < CoinJoinTransactions.Count; i++)
		{
			var item = CoinJoinTransactions[i];

			var transaction = new CoinJoinHistoryItemViewModel(
				i,
				item,
				_walletVm,
				balance,
				false);

			balance -= item.Amount;

			result.Add(transaction);
		}

		return result;
	}

	public override bool HasChildren()
	{
		if (CoinJoinTransactions.Count > 1)
		{
			return true;
		}

		return false;
	}

	public void Add(TransactionSummary item)
	{
		if (!item.IsOwnCoinjoin)
		{
			throw new InvalidOperationException("Not a coinjoin item!");
		}

		CoinJoinTransactions.Insert(0, item);
		Refresh();
	}

	private void Refresh()
	{
		IsConfirmed = CoinJoinTransactions.All(x => x.IsConfirmed());
		Date = CoinJoinTransactions.Select(tx => tx.DateTime).Max().ToLocalTime();
		OutgoingAmount = CoinJoinTransactions.Sum(x => x.Amount) * -1;
		UpdateDateString();
	}

	protected void UpdateDateString()
	{
		var dates = CoinJoinTransactions.Select(tx => tx.DateTime).ToImmutableArray();
		var firstDate = dates.Min().ToLocalTime();
		var lastDate = dates.Max().ToLocalTime();

		DateString = firstDate.Day == lastDate.Day
			? $"{firstDate:MM/dd/yyyy}"
			: $"{firstDate:MM/dd/yyyy} - {lastDate:MM/dd/yyyy}";
	}

	public void SetBalance(Money balance)
	{
		Balance = balance;
	}
}
