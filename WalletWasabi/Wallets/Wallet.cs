using Microsoft.Extensions.Hosting;
using NBitcoin;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Analysis.FeesEstimation;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Stores;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WebClients.PayJoin;

namespace WalletWasabi.Wallets;

public class Wallet : BackgroundService, IWallet
{
	private volatile WalletState _state;

	public Wallet(string dataDir, Network network, string filePath) : this(dataDir, network, KeyManager.FromFile(filePath))
	{
	}

	public Wallet(string dataDir, Network network, KeyManager keyManager)
	{
		Guard.NotNullOrEmptyOrWhitespace(nameof(dataDir), dataDir);
		Network = Guard.NotNull(nameof(network), network);
		KeyManager = Guard.NotNull(nameof(keyManager), keyManager);

		RuntimeParams.SetDataDir(dataDir);
		HandleFiltersLock = new AsyncLock();

		KeyManager.AssertCleanKeysIndexedAndPersist();

		if (!KeyManager.IsWatchOnly)
		{
			KeyChain = new KeyChain(KeyManager, Kitchen);
		}

		DestinationProvider = new InternalDestinationProvider(KeyManager);
	}

	public event EventHandler<ProcessedResult>? WalletRelevantTransactionProcessed;

	public event EventHandler<FilterModel>? NewFilterProcessed;

	public event EventHandler<Block>? NewBlockProcessed;

	public event EventHandler<WalletState>? StateChanged;

	public WalletState State
	{
		get => _state;
		private set
		{
			if (_state == value)
			{
				return;
			}

			_state = value;
			StateChanged?.Invoke(this, _state);
		}
	}

	public BitcoinStore BitcoinStore { get; private set; }
	public KeyManager KeyManager { get; }
	public WasabiSynchronizer Synchronizer { get; private set; }
	public ServiceConfiguration ServiceConfiguration { get; private set; }
	public string WalletName => KeyManager.WalletName;

	/// <summary>
	/// Unspent Transaction Outputs
	/// </summary>
	public ICoinsView Coins { get; private set; }

	public bool RedCoinIsolation => KeyManager.RedCoinIsolation;

	public Task<bool> IsWalletPrivateAsync() => Task.FromResult(IsWalletPrivate());

	public bool IsWalletPrivate() => GetPrivacyPercentage(new CoinsView(Coins), AnonScoreTarget) >= 1;

	public Task<IEnumerable<SmartCoin>> GetCoinjoinCoinCandidatesAsync() => Task.FromResult(GetCoinjoinCoinCandidates());

	public Task<IEnumerable<SmartTransaction>> GetTransactionsAsync() => Task.FromResult(GetTransactions());

	public IEnumerable<SmartTransaction> GetTransactions()
	{
		var walletTransactions = new List<SmartTransaction>();
		var allCoins = ((CoinsRegistry)Coins).AsAllCoinsView();
		foreach (SmartCoin coin in allCoins)
		{
			walletTransactions.Add(coin.Transaction);
			if (coin.SpenderTransaction is not null)
			{
				walletTransactions.Add(coin.SpenderTransaction);
			}
		}
		return walletTransactions.OrderByBlockchain().ToList();
	}

	public IEnumerable<SmartCoin> GetCoinjoinCoinCandidates() => Coins;

	private double GetPrivacyPercentage(CoinsView coins, int privateThreshold)
	{
		var privateAmount = coins.FilterBy(x => x.HdPubKey.AnonymitySet >= privateThreshold).TotalAmount();
		var normalAmount = coins.FilterBy(x => x.HdPubKey.AnonymitySet < privateThreshold).TotalAmount();

		var privateDecimalAmount = privateAmount.ToDecimal(MoneyUnit.BTC);
		var normalDecimalAmount = normalAmount.ToDecimal(MoneyUnit.BTC);
		var totalDecimalAmount = privateDecimalAmount + normalDecimalAmount;

		var pcPrivate = totalDecimalAmount == 0M ? 1d : (double)(privateDecimalAmount / totalDecimalAmount);
		return pcPrivate;
	}

	public Network Network { get; }
	public TransactionProcessor TransactionProcessor { get; private set; }

	public HybridFeeProvider FeeProvider { get; private set; }
	public FilterModel LastProcessedFilter { get; private set; }
	public IBlockProvider BlockProvider { get; private set; }
	private AsyncLock HandleFiltersLock { get; }

	public bool IsLoggedIn { get; private set; }

	public Kitchen Kitchen { get; } = new();

	public bool IsUnderPlebStop => Coins.TotalAmount() <= KeyManager.PlebStopThreshold;

	public bool TryLogin(string password, out string? compatibilityPasswordUsed)
	{
		compatibilityPasswordUsed = null;

		if (KeyManager.IsWatchOnly)
		{
			IsLoggedIn = true;
			Kitchen.Cook("");
		}
		else if (PasswordHelper.TryPassword(KeyManager, password, out compatibilityPasswordUsed))
		{
			IsLoggedIn = true;
			Kitchen.Cook(compatibilityPasswordUsed ?? Guard.Correct(password));
		}

		if (IsLoggedIn && KeyChain is KeyChain keychain)
		{
			keychain.PreloadMasterKey();
		}

		return IsLoggedIn;
	}

	public void Logout()
	{
		Kitchen.CleanUp();
		IsLoggedIn = false;
	}

	public void RegisterServices(
		BitcoinStore bitcoinStore,
		WasabiSynchronizer syncer,
		ServiceConfiguration serviceConfiguration,
		HybridFeeProvider feeProvider,
		IBlockProvider blockProvider)
	{
		if (State > WalletState.WaitingForInit)
		{
			throw new InvalidOperationException($"{nameof(State)} must be {WalletState.Uninitialized} or {WalletState.WaitingForInit}. Current state: {State}.");
		}

		try
		{
			BitcoinStore = Guard.NotNull(nameof(bitcoinStore), bitcoinStore);
			Synchronizer = Guard.NotNull(nameof(syncer), syncer);
			ServiceConfiguration = Guard.NotNull(nameof(serviceConfiguration), serviceConfiguration);
			FeeProvider = Guard.NotNull(nameof(feeProvider), feeProvider);

			TransactionProcessor = new TransactionProcessor(BitcoinStore.TransactionStore, KeyManager, ServiceConfiguration.DustThreshold);
			Coins = TransactionProcessor.Coins;

			TransactionProcessor.WalletRelevantTransactionProcessed += TransactionProcessor_WalletRelevantTransactionProcessed;
			BitcoinStore.IndexStore.NewFilter += IndexDownloader_NewFilterAsync;
			BitcoinStore.IndexStore.Reorged += IndexDownloader_ReorgedAsync;
			BitcoinStore.MempoolService.TransactionReceived += Mempool_TransactionReceived;

			BlockProvider = blockProvider;

			State = WalletState.Initialized;
		}
		catch
		{
			State = WalletState.Uninitialized;
			throw;
		}
	}

	/// <inheritdoc/>
	public override async Task StartAsync(CancellationToken cancel)
	{
		if (State != WalletState.Initialized)
		{
			throw new InvalidOperationException($"{nameof(State)} must be {WalletState.Initialized}. Current state: {State}.");
		}

		try
		{
			State = WalletState.Starting;

			if (!Synchronizer.IsRunning)
			{
				throw new NotSupportedException($"{nameof(Synchronizer)} is not running.");
			}

			using (BenchmarkLogger.Measure())
			{
				await RuntimeParams.LoadAsync().ConfigureAwait(false);

				using (await HandleFiltersLock.LockAsync(cancel).ConfigureAwait(false))
				{
					await LoadWalletStateAsync(cancel).ConfigureAwait(false);
					await LoadDummyMempoolAsync().ConfigureAwait(false);
				}
			}

			await base.StartAsync(cancel).ConfigureAwait(false);

			State = WalletState.Started;
		}
		catch
		{
			State = WalletState.Initialized;
			throw;
		}
	}

	/// <inheritdoc />
	protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

	/// <param name="allowUnconfirmed">Allow to spend unconfirmed transactions, if necessary.</param>
	/// <param name="allowedInputs">Only these inputs allowed to be used to build the transaction. The wallet must know the corresponding private keys.</param>
	/// <exception cref="ArgumentException"></exception>
	/// <exception cref="ArgumentNullException"></exception>
	/// <exception cref="ArgumentOutOfRangeException"></exception>
	public BuildTransactionResult BuildTransaction(
		string password,
		PaymentIntent payments,
		FeeStrategy feeStrategy,
		bool allowUnconfirmed = false,
		IEnumerable<OutPoint>? allowedInputs = null,
		IPayjoinClient? payjoinClient = null,
		bool tryToSign = true)
	{
		var builder = new TransactionFactory(Network, KeyManager, Coins, BitcoinStore.TransactionStore, password, allowUnconfirmed);
		return builder.BuildTransaction(
			payments,
			feeRateFetcher: () =>
			{
				if (feeStrategy.Type == FeeStrategyType.Target)
				{
					return FeeProvider.AllFeeEstimate?.GetFeeRate(feeStrategy.Target.Value) ?? throw new InvalidOperationException("Cannot get fee estimations.");
				}
				else if (feeStrategy.Type == FeeStrategyType.Rate)
				{
					return feeStrategy.Rate;
				}
				else
				{
					throw new NotSupportedException(feeStrategy.Type.ToString());
				}
			},
			allowedInputs,
			lockTimeSelector: () =>
			{
				var currentTipHeight = BitcoinStore.SmartHeaderChain.TipHeight;
				return LockTimeSelector.Instance.GetLockTimeBasedOnDistribution(currentTipHeight);
			},
			payjoinClient,
			tryToSign: tryToSign);
	}

	/// <inheritdoc/>
	public override async Task StopAsync(CancellationToken cancel)
	{
		try
		{
			var prevState = State;
			State = WalletState.Stopping;

			if (prevState < WalletState.Stopping)
			{
				await base.StopAsync(cancel).ConfigureAwait(false);

				if (prevState >= WalletState.Initialized)
				{
					BitcoinStore.IndexStore.NewFilter -= IndexDownloader_NewFilterAsync;
					BitcoinStore.IndexStore.Reorged -= IndexDownloader_ReorgedAsync;
					BitcoinStore.MempoolService.TransactionReceived -= Mempool_TransactionReceived;
					TransactionProcessor.WalletRelevantTransactionProcessed -= TransactionProcessor_WalletRelevantTransactionProcessed;
				}
			}
		}
		finally
		{
			State = WalletState.Stopped;
		}
	}

	private void TransactionProcessor_WalletRelevantTransactionProcessed(object? sender, ProcessedResult e)
	{
		try
		{
			WalletRelevantTransactionProcessed?.Invoke(this, e);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private void Mempool_TransactionReceived(object? sender, SmartTransaction tx)
	{
		try
		{
			if (!TransactionProcessor.IsAware(tx.GetHash()))
			{
				TransactionProcessor.Process(tx);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex);
		}
	}

	private async void IndexDownloader_ReorgedAsync(object? sender, FilterModel invalidFilter)
	{
		try
		{
			using (await HandleFiltersLock.LockAsync().ConfigureAwait(false))
			{
				uint256 invalidBlockHash = invalidFilter.Header.BlockHash;
				if (BlockProvider is CachedBlockProvider blockCache)
				{
					await blockCache.InvalidateAsync(invalidBlockHash, CancellationToken.None).ConfigureAwait(false);
				}

				KeyManager.SetMaxBestHeight(new Height(invalidFilter.Header.Height - 1));
				TransactionProcessor.UndoBlock((int)invalidFilter.Header.Height);
				BitcoinStore.TransactionStore.ReleaseToMempoolFromBlock(invalidBlockHash);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex);
		}
	}

	private async void IndexDownloader_NewFilterAsync(object? sender, FilterModel filterModel)
	{
		try
		{
			using (await HandleFiltersLock.LockAsync().ConfigureAwait(false))
			{
				if (KeyManager.GetBestHeight() < filterModel.Header.Height)
				{
					await ProcessFilterModelAsync(filterModel, CancellationToken.None).ConfigureAwait(false);
				}
			}

			NewFilterProcessed?.Invoke(this, filterModel);

			await Task.Delay(100).ConfigureAwait(false);

			if (Synchronizer is null || BitcoinStore?.SmartHeaderChain is null)
			{
				return;
			}

			// Make sure fully synced and this filter is the latest filter.
			if (BitcoinStore.SmartHeaderChain.HashesLeft != 0 || BitcoinStore.SmartHeaderChain.TipHash != filterModel.Header.BlockHash)
			{
				return;
			}

			var task = BitcoinStore.MempoolService?.TryPerformMempoolCleanupAsync(Synchronizer.HttpClientFactory);

			if (task is { })
			{
				await task.ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex);
		}
	}

	private async Task LoadWalletStateAsync(CancellationToken cancel)
	{
		KeyManager.AssertNetworkOrClearBlockState(Network);
		Height bestKeyManagerHeight = KeyManager.GetBestHeight();

		using (BenchmarkLogger.Measure(LogLevel.Info, "Initial Transaction Processing"))
		{
			TransactionProcessor.Process(BitcoinStore.TransactionStore.ConfirmedStore.GetTransactions().TakeWhile(x => x.Height <= bestKeyManagerHeight));
		}

		// Go through the filters and queue to download the matches.
		await BitcoinStore.IndexStore.ForeachFiltersAsync(async (filterModel) => await ProcessFilterModelAsync(filterModel, cancel).ConfigureAwait(false),
			new Height(bestKeyManagerHeight.Value + 1), cancel).ConfigureAwait(false);
	}

	private async Task LoadDummyMempoolAsync()
	{
		if (BitcoinStore.TransactionStore.MempoolStore.IsEmpty())
		{
			return;
		}

		// Only clean the mempool if we're fully synchronized.
		if (BitcoinStore.SmartHeaderChain.HashesLeft == 0)
		{
			try
			{
				var client = Synchronizer.HttpClientFactory.SharedWasabiClient;
				var compactness = 10;

				var mempoolHashes = await client.GetMempoolHashesAsync(compactness).ConfigureAwait(false);

				var txsToProcess = new List<SmartTransaction>();
				foreach (var tx in BitcoinStore.TransactionStore.MempoolStore.GetTransactions())
				{
					uint256 hash = tx.GetHash();
					if (mempoolHashes.Contains(hash.ToString()[..compactness]))
					{
						txsToProcess.Add(tx);
						Logger.LogInfo($"'{WalletName}': Transaction was successfully tested against the backend's mempool hashes: {hash}.");
					}
					else
					{
						BitcoinStore.TransactionStore.MempoolStore.TryRemove(tx.GetHash(), out _);
					}
				}

				TransactionProcessor.Process(txsToProcess);
			}
			catch (Exception ex)
			{
				// When there's a connection failure do not clean the transactions, add them to processing.
				TransactionProcessor.Process(BitcoinStore.TransactionStore.MempoolStore.GetTransactions());

				Logger.LogWarning(ex);
			}
		}
		else
		{
			TransactionProcessor.Process(BitcoinStore.TransactionStore.MempoolStore.GetTransactions());
		}
	}

	private async Task ProcessFilterModelAsync(FilterModel filterModel, CancellationToken cancel)
	{
		var matchFound = filterModel.Filter.MatchAny(KeyManager.GetPubKeyScriptBytes(), filterModel.FilterKey);
		if (matchFound)
		{
			Block currentBlock = await BlockProvider.GetBlockAsync(filterModel.Header.BlockHash, cancel).ConfigureAwait(false); // Wait until not downloaded.
			var height = new Height(filterModel.Header.Height);

			var txsToProcess = new List<SmartTransaction>();
			for (int i = 0; i < currentBlock.Transactions.Count; i++)
			{
				Transaction tx = currentBlock.Transactions[i];
				txsToProcess.Add(new SmartTransaction(tx, height, currentBlock.GetHash(), i, firstSeen: currentBlock.Header.BlockTime, label: BitcoinStore.MempoolService.TryGetLabel(tx.GetHash())));
			}

			TransactionProcessor.Process(txsToProcess);
			KeyManager.SetBestHeight(height);

			NewBlockProcessed?.Invoke(this, currentBlock);
		}

		LastProcessedFilter = filterModel;
	}

	public void SetWaitingForInitState()
	{
		if (State != WalletState.Uninitialized)
		{
			throw new InvalidOperationException($"{nameof(State)} must be {WalletState.Uninitialized}. Current state: {State}.");
		}

		State = WalletState.WaitingForInit;
	}

	public static Wallet CreateAndRegisterServices(Network network, BitcoinStore bitcoinStore, KeyManager keyManager, WasabiSynchronizer synchronizer, string dataDir, ServiceConfiguration serviceConfiguration, HybridFeeProvider feeProvider, IBlockProvider blockProvider)
	{
		var wallet = new Wallet(dataDir, network, keyManager);
		wallet.RegisterServices(bitcoinStore, synchronizer, serviceConfiguration, feeProvider, blockProvider);
		return wallet;
	}

	public bool IsMixable =>
		State == WalletState.Started // Only running wallets
		&& !KeyManager.IsWatchOnly // that are not watch-only wallets
		&& Kitchen.HasIngredients;

	public IKeyChain? KeyChain { get; }

	public IDestinationProvider DestinationProvider { get; }
	public int AnonScoreTarget => KeyManager.AnonScoreTarget;
	public bool ConsolidationMode => false;
	public TimeSpan FeeRateMedianTimeFrame => TimeSpan.FromHours(KeyManager.FeeRateMedianTimeFrameHours);
}
