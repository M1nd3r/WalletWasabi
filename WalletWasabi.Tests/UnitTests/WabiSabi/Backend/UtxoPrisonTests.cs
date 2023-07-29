using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Backend.DoSPrevention;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Backend;

public class UtxoPrisonTests
{
	[Fact]
	public async Task OffensesAreSaved()
	{
		var (prison, reader, _) = WabiSabiFactory.CreateObservablePrison();

		var outpoint = BitcoinFactory.CreateOutPoint();
		var roundId = BitcoinFactory.CreateUint256();

		// Fail to verify
		prison.FailedVerification(outpoint, roundId);
		var offenderToSave = await reader.ReadAsync().ConfigureAwait(false);
		var failedToVerify = Assert.IsType<FailedToVerify>(offenderToSave.Offense);
		Assert.Equal(outpoint, offenderToSave.OutPoint);
		Assert.Equal(roundId, failedToVerify.VerifiedInRoundId);

		// Fail to confirm
		prison.FailedToConfirm(outpoint, Money.Coins(1m), roundId);
		offenderToSave = await reader.ReadAsync().ConfigureAwait(false);
		var disruptionNotConfirming = Assert.IsType<RoundDisruption>(offenderToSave.Offense);
		Assert.Equal(outpoint, offenderToSave.OutPoint);
		Assert.Equal(roundId, disruptionNotConfirming.DisruptedRoundId);
		Assert.Equal(RoundDisruptionMethod.DidNotConfirm, disruptionNotConfirming.Method);
		Assert.Equal(Money.Coins(1m), disruptionNotConfirming.Value);

		// Fail to sign
		prison.FailedToSign(outpoint, Money.Coins(2m), roundId);
		offenderToSave = await reader.ReadAsync().ConfigureAwait(false);
		var disruptionNotSigning = Assert.IsType<RoundDisruption>(offenderToSave.Offense);
		Assert.Equal(outpoint, offenderToSave.OutPoint);
		Assert.Equal(roundId, disruptionNotSigning.DisruptedRoundId);
		Assert.Equal(RoundDisruptionMethod.DidNotSign, disruptionNotSigning.Method);
		Assert.Equal(Money.Coins(2m), disruptionNotSigning.Value);

		// Double spent
		prison.DoubleSpent(outpoint, Money.Coins(3m), roundId);
		offenderToSave = await reader.ReadAsync().ConfigureAwait(false);
		var doubleSpending = Assert.IsType<RoundDisruption>(offenderToSave.Offense);
		Assert.Equal(outpoint, offenderToSave.OutPoint);
		Assert.Equal(roundId, doubleSpending.DisruptedRoundId);
		Assert.Equal(RoundDisruptionMethod.DoubleSpent, doubleSpending.Method);
		Assert.Equal(Money.Coins(3m), doubleSpending.Value);

		// Cheating
		prison.CheatingDetected(outpoint, roundId);
		offenderToSave = await reader.ReadAsync().ConfigureAwait(false);
		var cheating = Assert.IsType<Cheating>(offenderToSave.Offense);
		Assert.Equal(outpoint, offenderToSave.OutPoint);
		Assert.Equal(roundId, cheating.RoundId);

		Assert.Equal(0, reader.Count);
	}

	[Fact]
	public void BanningTime()
	{
		var (prison, _, cfg) = WabiSabiFactory.CreateObservablePrison();

		var roundId = BitcoinFactory.CreateUint256();

		// Failed to verify punishment is constant (not affected by number of attempts)
		var ftvOutpoint = BitcoinFactory.CreateOutPoint();
		prison.FailedVerification(ftvOutpoint, roundId);
		prison.FailedVerification(ftvOutpoint, roundId);
		prison.FailedVerification(ftvOutpoint, roundId);
		var banningPeriod = prison.GetBanTimePeriod(ftvOutpoint);
		Assert.Equal(cfg.MinTimeForFailedToVerify, banningPeriod.Duration);

		// Cheating punishment is constant (not affected by number of attempts)
		var chtOutpoint = BitcoinFactory.CreateOutPoint();
		prison.CheatingDetected(chtOutpoint, roundId);
		prison.CheatingDetected(chtOutpoint, roundId);
		prison.CheatingDetected(chtOutpoint, roundId);
		banningPeriod = prison.GetBanTimePeriod(chtOutpoint);
		Assert.Equal(cfg.MinTimeForCheating, banningPeriod.Duration);

		// Failed to confirm is calculated and inversely proportional to the amount
		var ftcOutpoint1 = BitcoinFactory.CreateOutPoint();
		var ftcOutpoint2 = BitcoinFactory.CreateOutPoint();
		prison.FailedToConfirm(ftcOutpoint1, Money.Coins(0.5m), roundId);
		prison.FailedToConfirm(ftcOutpoint2, Money.Coins(1.0m), roundId);

		var banningPeriod1 = prison.GetBanTimePeriod(ftcOutpoint1);
		var banningPeriod2 = prison.GetBanTimePeriod(ftcOutpoint2);
		Assert.True(banningPeriod1.Duration == 2 * banningPeriod2.Duration);

		// .... second attempt is punished harder
		prison.FailedToConfirm(ftcOutpoint1, Money.Coins(0.5m), roundId);
		var banningPeriod1FailedToConfirmTwice = prison.GetBanTimePeriod(ftcOutpoint1);
		Assert.True(banningPeriod1FailedToConfirmTwice.Duration > banningPeriod1.Duration);

		// .... the worst offense is applied
		// Note: this case is compared against ftcOutpoint1 which failed to confirm twice
		var ftcOutpoint3 = BitcoinFactory.CreateOutPoint();
		prison.FailedToConfirm(ftcOutpoint3, Money.Coins(0.5m), roundId);
		prison.FailedToSign(ftcOutpoint3, Money.Coins(0.5m), roundId);

		var banningPeriod3FailedToConfirmAndSign = prison.GetBanTimePeriod(ftcOutpoint3);
		Assert.True(banningPeriod3FailedToConfirmAndSign.Duration > banningPeriod1FailedToConfirmTwice.Duration);

		// Big amounts are not banned the first time
		var ftcOutpointBig = BitcoinFactory.CreateOutPoint();
		prison.FailedToConfirm(ftcOutpointBig, Money.Coins(2m), roundId);
		var banningPeriodBigCoin = prison.GetBanTimePeriod(ftcOutpointBig);
		Assert.Equal(TimeSpan.Zero, banningPeriodBigCoin.Duration);

		// ... however, second attempts could be punished
		prison.FailedToConfirm(ftcOutpointBig, Money.Coins(2m), roundId);
		banningPeriodBigCoin = prison.GetBanTimePeriod(ftcOutpointBig);
		Assert.NotEqual(TimeSpan.Zero, banningPeriodBigCoin.Duration);

		// ... except if they come from previous coinjoins
		var ftcOutpointBigPaid = new OutPoint(uint256.One, 1); // tx 0000....0000001 is a coinjoin
		prison.FailedToConfirm(ftcOutpointBigPaid, Money.Coins(2m), roundId);
		var banningPeriodBigPaidCoin = prison.GetBanTimePeriod(ftcOutpointBigPaid);
		Assert.Equal(TimeSpan.Zero, banningPeriodBigPaidCoin.Duration); // it is not banned first time

		prison.FailedToConfirm(ftcOutpointBigPaid, Money.Coins(2m), roundId);
		banningPeriodBigPaidCoin = prison.GetBanTimePeriod(ftcOutpointBigPaid);
		Assert.Equal(TimeSpan.Zero, banningPeriodBigPaidCoin.Duration); // it is not banned second time either
	}
}
