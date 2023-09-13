using Avalonia;
using DynamicData;
using DynamicData.Binding;
using NBitcoin;
using ReactiveUI;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Home.Tiles.PrivacyRing;

[NavigationMetaData(
	Title = "Privacy Progress",
	NavigationTarget = NavigationTarget.DialogScreen)]
public partial class PrivacyRingViewModel : RoutableViewModel
{
	private readonly CompositeDisposable _disposables = new();
	private readonly IWalletModel _wallet;

	[AutoNotify] private PrivacyRingItemViewModel? _selectedItem;
	[AutoNotify] private double _height;
	[AutoNotify] private double _width;
	[AutoNotify] private Thickness _margin;
	[AutoNotify] private Thickness _negativeMargin;

	public PrivacyRingViewModel(UiContext uiContext, IWalletModel wallet)
	{
		UiContext = uiContext;
		_wallet = wallet;

		NextCommand = CancelCommand;
		PrivacyTile = new PrivacyControlTileViewModel(UiContext, wallet, false);
		PrivacyTile.Activate(_disposables);

		PreviewItems.Add(PrivacyTile);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
	}

	public PrivacyControlTileViewModel PrivacyTile { get; }

	public ObservableCollectionExtended<PrivacyRingItemViewModel> Items { get; } = new();
	public ObservableCollectionExtended<PrivacyRingItemViewModel> References { get; } = new();
	public ObservableCollectionExtended<IPrivacyRingPreviewItem> PreviewItems { get; } = new();

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Uses DisposeWith()")]
	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		var itemsSourceList = new SourceList<PrivacyRingItemViewModel>();

		itemsSourceList
			.DisposeWith(disposables)
			.Connect()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Bind(Items)
			.DisposeMany()
			.Subscribe()
			.DisposeWith(disposables);

		var sizeTrigger =
			this.WhenAnyValue(x => x.Width, x => x.Height)
				.Where(tuple => tuple.Item1 != 0 && tuple.Item2 != 0)
				.Do(_ => itemsSourceList.Clear())
				.Throttle(TimeSpan.FromMilliseconds(100))
				.ToSignal();

		_wallet.Privacy.ProgressUpdated
					   .Merge(sizeTrigger)
					   .CombineLatest(_wallet.Coins.List.ToCollection().Select(x => x.Distinct()))
					   .Throttle(TimeSpan.FromMilliseconds(250))
					   .ObserveOn(RxApp.MainThreadScheduler)
					   .Do(t => RenderRing(itemsSourceList, t.Second))
					   .Subscribe()
					   .DisposeWith(disposables);
	}

	private void RenderRing(SourceList<PrivacyRingItemViewModel> list, IEnumerable<ICoinModel> coins)
	{
		if (Width == 0d)
		{
			return;
		}

		SetMargins();

		list.Edit(list => CreateSegments(list, coins));

		PreviewItems.RemoveMany(PreviewItems.OfType<PrivacyRingItemViewModel>());
		PreviewItems.AddRange(list.Items);

		SetReferences(list);
	}

	private void CreateSegments(IExtendedList<PrivacyRingItemViewModel> list, IEnumerable<ICoinModel> coins)
	{
		list.Clear();

		var coinCount = coins.Count();

		var shouldCreateSegmentsByCoin = coinCount < UiConstants.PrivacyRingMaxItemCount;

		var result =
			shouldCreateSegmentsByCoin
			? CreateSegmentsByCoin(coins)
			: CreateSegmentsByPrivacyLevel(coins);

		list.AddRange(result);
	}

	private IEnumerable<PrivacyRingItemViewModel> CreateSegmentsByCoin(IEnumerable<ICoinModel> coins)
	{
		var groupsByPrivacy =
			coins.GroupBy(x => x.PrivacyLevel)
				 .OrderBy(x => (int)x.Key)
				 .ToList();

		var total = coins.TotalBtcAmount();
		var start = 0.0m;

		foreach (var group in groupsByPrivacy)
		{
			var groupCoins = group.OrderByDescending(x => x.Amount).ToList();

			foreach (var coin in groupCoins)
			{
				var end = start + (Math.Abs(coin.Amount.ToDecimal(MoneyUnit.BTC)) / total);

				var item = new PrivacyRingItemViewModel(this, coin, (double)start, (double)end);

				yield return item;

				start = end;
			}
		}
	}

	private IEnumerable<PrivacyRingItemViewModel> CreateSegmentsByPrivacyLevel(IEnumerable<ICoinModel> coins)
	{
		var total = coins.TotalBtcAmount();
		var start = 0.0m;

		var groupsByPrivacy =
			coins.GroupBy(x => x.PrivacyLevel)
				 .OrderBy(x => (int)x.Key)
				 .ToList();

		foreach (var group in groupsByPrivacy)
		{
			var groupAmount =
				group.Sum(x => x.Amount.ToDecimal(MoneyUnit.BTC));

			var end = start + (Math.Abs(groupAmount) / total);

			var item = new PrivacyRingItemViewModel(this, group.Key, new Money(groupAmount, MoneyUnit.BTC), (double)start, (double)end);

			yield return item;

			start = end;
		}
	}

	private void SetMargins()
	{
		Margin = new Thickness(Width / 2, Height / 2, 0, 0);
		NegativeMargin = Margin * -1;
	}

	private void SetReferences(SourceList<PrivacyRingItemViewModel> list)
	{
		References.Clear();

		var references =
			list.Items.GroupBy(x => (x.IsPrivate, x.IsSemiPrivate, x.IsNonPrivate, x.Unconfirmed))
				.Select(x => x.First())
				.OrderBy(list.Items.IndexOf)
				.ToList();

		References.AddRange(references);
	}
}
