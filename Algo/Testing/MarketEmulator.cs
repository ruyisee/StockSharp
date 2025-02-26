#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Testing.Algo
File: MarketEmulator.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License

//#define EMU_DBG

namespace StockSharp.Algo.Testing
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;

	using StockSharp.Algo.Commissions;
	using StockSharp.Algo.PnL;
	using StockSharp.Logging;
	using StockSharp.Messages;
	using StockSharp.Algo.Candles;
	using StockSharp.Localization;
	using StockSharp.BusinessEntities;
	using StockSharp.Algo.Storages;

	using QuotesDict = System.Collections.Generic.SortedDictionary<decimal, Ecng.Common.RefPair<LevelQuotes, Messages.QuoteChange>>;

	class LevelQuotes : IEnumerable<ExecutionMessage>
	{
		private readonly List<ExecutionMessage> _quotes = new(5);
		private readonly Dictionary<long, ExecutionMessage> _quotesByTrId = new();

		public int Count => _quotes.Count;

		public ExecutionMessage this[int i]
		{
			get => _quotes[i];
			set
			{
				var prev = _quotes[i];

				if (prev.TransactionId != 0)
					_quotesByTrId.Remove(prev.TransactionId);

				_quotes[i] = value;

				if (value.TransactionId != 0)
					_quotesByTrId[value.TransactionId] = value;
			}
		}

		public bool TryGetByTransactionId(long transactionId, out ExecutionMessage msg) => _quotesByTrId.TryGetValue(transactionId, out msg);

		public void Add(ExecutionMessage quote)
		{
			if (quote.TransactionId != 0)
				_quotesByTrId[quote.TransactionId] = quote;

			_quotes.Add(quote);
		}

		public void RemoveAt(int index, ExecutionMessage quote = null)
		{
			quote ??= _quotes[index];

			_quotes.RemoveAt(index);

			if (quote.TransactionId != 0)
				_quotesByTrId.Remove(quote.TransactionId);
		}

		public void Remove(ExecutionMessage quote) => RemoveAt(_quotes.IndexOf(quote), quote);

		public IEnumerator<ExecutionMessage> GetEnumerator() => _quotes.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	/// <summary>
	/// Emulator.
	/// </summary>
	public class MarketEmulator : BaseLogReceiver, IMarketEmulator
	{
		private abstract class Pool<T>
		{
			private readonly Queue<T> _pool = new();

			public T Allocate()
			{
				if (_pool.Count == 0)
					return Create();
				else
					return _pool.Dequeue();
			}

			protected abstract T Create();

			public void Free(T message) => _pool.Enqueue(message);
		}

		private class ExecMsgPool : Pool<ExecutionMessage>
		{
			protected override ExecutionMessage Create() => new() { DataTypeEx = DataType.Transactions };
		}

		private class TickPool : Pool<(Sides? side, decimal price, decimal vol)[]>
		{
			protected override (Sides?, decimal, decimal)[] Create()
				=> new (Sides?, decimal, decimal)[4];
		}

		private sealed class SecurityMarketEmulator : BaseLogReceiver//, IMarketEmulator
		{
			private readonly MarketEmulator _parent;
			private readonly SecurityId _securityId;

			private readonly Dictionary<ExecutionMessage, TimeSpan> _expirableOrders = new();
			private readonly Dictionary<long, ExecutionMessage> _activeOrders = new();
			private readonly QuotesDict _bids = new(new BackwardComparer<decimal>());
			private readonly QuotesDict _asks = new();
			private readonly Dictionary<ExecutionMessage, TimeSpan> _pendingExecutions = new();
			private DateTimeOffset _prevTime;
			private readonly MarketEmulatorSettings _settings;
			private readonly Random _volumeRandom = new(TimeHelper.Now.Millisecond);
			private readonly Random _priceRandom = new(TimeHelper.Now.Millisecond);
			private readonly RandomArray<bool> _isMatch = new(100);
			private int _volumeDecimals;
			private readonly SortedDictionary<DateTimeOffset, List<(CandleMessage candle, (Sides? side, decimal price, decimal vol)[] ticks)>> _candleInfo = new();
			private LogLevels? _logLevel;
			private DateTime _lastStripDate;

			private decimal _totalBidVolume;
			private decimal _totalAskVolume;

			private long? _depthSubscription;
			private long? _ticksSubscription;
			private long? _l1Subscription;
			private long? _olSubscription;

			private bool _priceStepUpdated;
			private bool _volumeStepUpdated;
			private bool _priceStepExplicit;
			private bool _volumeStepExplicit;

			private decimal _prevTickPrice;
			private decimal _currSpreadPrice;

			private decimal? _l1BidPrice;
			private decimal? _l1AskPrice;
			private decimal? _l1BidVol;
			private decimal? _l1AskVol;

			// указывает, есть ли реальные стаканы, чтобы своей псевдо генерацией не портить настоящую историю
			private DateTime _lastDepthDate;
			//private DateTime _lastTradeDate;

			private readonly ExecMsgPool _messagePool = new();
			private readonly TickPool _tickPool = new();

			private OrderBookIncrementBuilder _bookBuilder;
			private bool? _booksWithState;

			public SecurityMarketEmulator(MarketEmulator parent, SecurityId securityId)
			{
				_parent = parent ?? throw new ArgumentNullException(nameof(parent));
				_securityId = securityId;
				_settings = parent.Settings;
			}

			private SecurityMessage _securityDefinition;
			public SecurityMessage SecurityDefinition => _securityDefinition;

			private void LogMessage(Message message, bool isInput)
			{
				_logLevel ??= this.GetLogLevel();

				if (_logLevel != LogLevels.Debug)
					return;

				if (message.Type is not MessageTypes.Time
					and not MessageTypes.Level1Change
					and not MessageTypes.QuoteChange)
					this.AddDebugLog((isInput ? " --> {0}" : " <-- {0}"), message);
			}

			public void Process(Message message, ICollection<Message> result)
			{
				if (_prevTime == DateTimeOffset.MinValue)
					_prevTime = message.LocalTime;

				LogMessage(message, true);

				switch (message.Type)
				{
					case MessageTypes.Time:
						//ProcessTimeMessage((TimeMessage)message, result);
						break;

					case MessageTypes.Execution:
					{
						var execMsg = (ExecutionMessage)message;

						UpdatePriceLimits(execMsg, result);

						if (execMsg.DataType == DataType.Ticks)
						{
							ProcessTick(execMsg, result);
						}
						else if (execMsg.DataType == DataType.Transactions)
						{
							if (!execMsg.HasOrderInfo())
								throw new InvalidOperationException();

							if (_settings.Latency > TimeSpan.Zero)
							{
								this.AddInfoLog(LocalizedStrings.Str1145Params, execMsg.IsCancellation ? LocalizedStrings.Str1146 : LocalizedStrings.Str1147, execMsg.TransactionId == 0 ? execMsg.OriginalTransactionId : execMsg.TransactionId);
								_pendingExecutions.Add(execMsg.TypedClone(), _settings.Latency);
							}
							else
								AcceptExecution(execMsg.LocalTime, execMsg, result);
						}
						else if (execMsg.DataType == DataType.OrderLog)
						{
							if (execMsg.TradeId == null)
								UpdateQuotes(execMsg, result);

							// добавляем в результат ОЛ только из хранилища или из генератора
							// (не из ExecutionLogConverter)
							//if (execMsg.TransactionId > 0)
							//	result.Add(execMsg);

							if (_olSubscription is not null)
								result.Add(execMsg);
						}
						else
							throw new ArgumentOutOfRangeException(nameof(message), execMsg.DataType, LocalizedStrings.Str1219);

						break;
					}

					case MessageTypes.OrderRegister:
					{
						var orderMsg = (OrderRegisterMessage)message;

						foreach (var m in ToExecutionLog(orderMsg, GetTotalVolume(orderMsg.Side.Invert())))
							Process(m, result);

						break;
					}

					case MessageTypes.OrderCancel:
					{
						var orderMsg = (OrderCancelMessage)message;

						foreach (var m in ToExecutionLog(orderMsg, 0))
							Process(m, result);

						break;
					}

					case MessageTypes.OrderReplace:
					{
						//при перерегистрации могут приходить заявки с нулевым объемом
						//объем при этом надо взять из старой заявки.
						var orderMsg = (OrderReplaceMessage)message;
						var oldOrder = _activeOrders.TryGetValue(orderMsg.OriginalTransactionId);

						foreach (var execMsg in ToExecutionLog(orderMsg, GetTotalVolume(orderMsg.Side.Invert())))
						{
							if (oldOrder != null)
							{
								if (!execMsg.IsCancellation && execMsg.OrderVolume == 0)
									execMsg.OrderVolume = oldOrder.Balance;

								Process(execMsg, result);
							}
							else if (execMsg.IsCancellation)
							{
								var error = LocalizedStrings.Str1148Params.Put(execMsg.OrderId);
								var serverTime = GetServerTime(orderMsg.LocalTime);

								// cancellation error
								result.Add(new ExecutionMessage
								{
									LocalTime = orderMsg.LocalTime,
									OriginalTransactionId = orderMsg.TransactionId,
									OrderId = execMsg.OrderId,
									DataTypeEx = DataType.Transactions,
									SecurityId = orderMsg.SecurityId,
									IsCancellation = true,
									OrderState = OrderStates.Failed,
									Error = new InvalidOperationException(error),
									ServerTime = serverTime,
									HasOrderInfo = true,
									StrategyId = orderMsg.StrategyId,
								});

								// registration error
								result.Add(new ExecutionMessage
								{
									LocalTime = orderMsg.LocalTime,
									OriginalTransactionId = orderMsg.TransactionId,
									DataTypeEx = DataType.Transactions,
									SecurityId = orderMsg.SecurityId,
									IsCancellation = false,
									OrderState = OrderStates.Failed,
									Error = new InvalidOperationException(error),
									ServerTime = serverTime,
									HasOrderInfo = true,
									StrategyId = orderMsg.StrategyId,
								});

								this.AddErrorLog(LocalizedStrings.Str1148Params, orderMsg.OriginalTransactionId);
							}
						}

						break;
					}

					case MessageTypes.OrderStatus:
					{
						var statusMsg = (OrderStatusMessage)message;
						var checkByPf = !statusMsg.PortfolioName.IsEmpty();

						var finish = false;

						foreach (var order in _activeOrders.Values)
						{
							if (checkByPf)
							{
								if (!order.PortfolioName.EqualsIgnoreCase(statusMsg.PortfolioName))
									continue;
							}
							else if (statusMsg.OrderId != null)
							{
								if (order.OrderId != statusMsg.OrderId)
									continue;

								finish = true;
							}

							var clone = order.TypedClone();
							clone.OriginalTransactionId = statusMsg.TransactionId;
							result.Add(clone);

							if (finish)
								break;
						}

						break;
					}

					case MessageTypes.QuoteChange:
						ProcessQuoteChange((QuoteChangeMessage)message, result);
						break;

					case MessageTypes.Level1Change:
						ProcessLevel1((Level1ChangeMessage)message, result);
						break;

					case MessageTypes.Security:
					{
						_securityDefinition = (SecurityMessage)message.Clone();
						_volumeDecimals = GetVolumeStep().GetCachedDecimals();
						UpdateSecurityDefinition(_securityDefinition);
						break;
					}

					case MessageTypes.Board:
					{
						//_execLogConverter.UpdateBoardDefinition((BoardMessage)message);
						break;
					}

					case MessageTypes.MarketData:
					{
						var mdMsg = (MarketDataMessage)message;

						if (mdMsg.IsSubscribe)
						{
							if (mdMsg.DataType2 == DataType.MarketDepth)
								_depthSubscription = mdMsg.TransactionId;
							else if (mdMsg.DataType2 == DataType.Ticks)
								_ticksSubscription = mdMsg.TransactionId;
							else if (mdMsg.DataType2 == DataType.Level1)
								_l1Subscription = mdMsg.TransactionId;
							else if (mdMsg.DataType2 == DataType.OrderLog)
								_olSubscription = mdMsg.TransactionId;
						}
						else
						{
							if (_depthSubscription == mdMsg.OriginalTransactionId)
								_depthSubscription = null;
							else if (_ticksSubscription == mdMsg.OriginalTransactionId)
								_ticksSubscription = null;
							else if (_l1Subscription == mdMsg.OriginalTransactionId)
								_l1Subscription = null;
							else if (_olSubscription == mdMsg.OriginalTransactionId)
								_olSubscription = null;
						}

						break;
					}

					default:
					{
						if (message is CandleMessage candleMsg)
						{
							// в трейдах используется время открытия свечи, при разных MarketTimeChangedInterval и TimeFrame свечек
							// возможны ситуации, когда придет TimeMsg 11:03:00, а время закрытия будет 11:03:30
							// т.о. время уйдет вперед данных, которые построены по свечкам.
							var candles = _candleInfo.SafeAdd(candleMsg.OpenTime, key => new());

							var ticks = _tickPool.Allocate();
							candles.Add((candleMsg, ticks));

							if (_securityDefinition != null/* && _parent._settings.UseCandlesTimeFrame != null*/)
							{
								candleMsg.ConvertToTrades(GetVolumeStep(), _volumeDecimals, ticks);
								ProcessTick(candleMsg.SecurityId, candleMsg.LocalTime, candleMsg.OpenTime, ticks[0], result);
							}

							break;
						}

						throw new ArgumentOutOfRangeException(nameof(message), message.Type, LocalizedStrings.Str1219);
					}
				}

				ProcessTime(message, result);

				_prevTime = message.LocalTime;

				foreach (var item in result)
					LogMessage(item, false);
			}

			private ExecutionMessage CreateMessage(DateTimeOffset localTime, DateTimeOffset serverTime, Sides side, decimal price, decimal volume, bool isCancelling = false, TimeInForce tif = TimeInForce.PutInQueue)
			{
				if (price <= 0)
					throw new ArgumentOutOfRangeException(nameof(price), price, LocalizedStrings.Str1144);

				if (volume <= 0)
					throw new ArgumentOutOfRangeException(nameof(volume), volume, LocalizedStrings.Str3344);

				return new()
				{
					Side = side,
					OrderPrice = price,
					OrderVolume = volume,
					DataTypeEx = DataType.OrderLog,
					IsCancellation = isCancelling,
					SecurityId = _securityId,
					LocalTime = localTime,
					ServerTime = serverTime,
					TimeInForce = tif,
				};
			}

			private IEnumerable<ExecutionMessage> ToExecutionLog(OrderMessage message, decimal quotesVolume)
			{
				var serverTime = GetServerTime(message.LocalTime);
				var priceStep = GetPriceStep();

				bool NeedCheckVolume(OrderRegisterMessage message)
				{
					if (!_settings.IncreaseDepthVolume)
						return false;

					var orderSide = message.Side;
					var price = message.Price;

					var quotes = GetQuotes(orderSide.Invert());

					var quote = quotes.FirstOrDefault();

					if (quote.Value == null)
						return false;

					var bestPrice = quote.Key;

					return (orderSide == Sides.Buy ? price >= bestPrice : price <= bestPrice)
						&& quotesVolume <= message.Volume;
				}

				IEnumerable<ExecutionMessage> IncreaseDepthVolume(OrderRegisterMessage message)
				{
					var leftVolume = (message.Volume - quotesVolume) + 1;
					var orderSide = message.Side;

					var quotes = GetQuotes(orderSide.Invert());
					var quote = quotes.LastOrDefault();

					if (quote.Value == null)
						yield break;

					var side = orderSide.Invert();

					var lastVolume = quote.Value.Second.Volume;
					var lastPrice = quote.Value.Second.Price;

					while (leftVolume > 0 && lastPrice != 0)
					{
						lastVolume *= 2;
						lastPrice += priceStep * (side == Sides.Buy ? -1 : 1);

						leftVolume -= lastVolume;

						yield return CreateMessage(message.LocalTime, serverTime, side, lastPrice, lastVolume);
					}
				}

				switch (message.Type)
				{
					case MessageTypes.OrderRegister:
					{
						var regMsg = (OrderRegisterMessage)message;

						if (NeedCheckVolume(regMsg))
						{
							foreach (var executionMessage in IncreaseDepthVolume(regMsg))
								yield return executionMessage;
						}

						yield return new ExecutionMessage
						{
							LocalTime = regMsg.LocalTime,
							ServerTime = serverTime,
							SecurityId = regMsg.SecurityId,
							DataTypeEx = DataType.Transactions,
							HasOrderInfo = true,
							TransactionId = regMsg.TransactionId,
							OrderPrice = regMsg.Price,
							OrderVolume = regMsg.Volume,
							Side = regMsg.Side,
							PortfolioName = regMsg.PortfolioName,
							OrderType = regMsg.OrderType,
							UserOrderId = regMsg.UserOrderId,
							StrategyId = regMsg.StrategyId,
						};

						yield break;
					}
					case MessageTypes.OrderReplace:
					{
						var replaceMsg = (OrderReplaceMessage)message;

						if (NeedCheckVolume(replaceMsg))
						{
							foreach (var executionMessage in IncreaseDepthVolume(replaceMsg))
								yield return executionMessage;
						}

						yield return new ExecutionMessage
						{
							LocalTime = replaceMsg.LocalTime,
							ServerTime = serverTime,
							SecurityId = replaceMsg.SecurityId,
							DataTypeEx = DataType.Transactions,
							HasOrderInfo = true,
							IsCancellation = true,
							OrderId = replaceMsg.OldOrderId,
							OriginalTransactionId = replaceMsg.OriginalTransactionId,
							TransactionId = replaceMsg.TransactionId,
							PortfolioName = replaceMsg.PortfolioName,
							OrderType = replaceMsg.OrderType,
							StrategyId = replaceMsg.StrategyId,
							// для старой заявки пользовательский идентификатор менять не надо
							//UserOrderId = replaceMsg.UserOrderId
						};

						yield return new ExecutionMessage
						{
							LocalTime = replaceMsg.LocalTime,
							ServerTime = serverTime,
							SecurityId = replaceMsg.SecurityId,
							DataTypeEx = DataType.Transactions,
							HasOrderInfo = true,
							TransactionId = replaceMsg.TransactionId,
							OrderPrice = replaceMsg.Price,
							OrderVolume = replaceMsg.Volume,
							Side = replaceMsg.Side,
							PortfolioName = replaceMsg.PortfolioName,
							OrderType = replaceMsg.OrderType,
							UserOrderId = replaceMsg.UserOrderId,
							StrategyId = replaceMsg.StrategyId,
						};

						yield break;
					}
					case MessageTypes.OrderCancel:
					{
						var cancelMsg = (OrderCancelMessage)message;

						yield return new ExecutionMessage
						{
							DataTypeEx = DataType.Transactions,
							HasOrderInfo = true,
							IsCancellation = true,
							OrderId = cancelMsg.OrderId,
							TransactionId = cancelMsg.TransactionId,
							OriginalTransactionId = cancelMsg.OriginalTransactionId,
							PortfolioName = cancelMsg.PortfolioName,
							SecurityId = cancelMsg.SecurityId,
							LocalTime = cancelMsg.LocalTime,
							ServerTime = serverTime,
							OrderType = cancelMsg.OrderType,
							StrategyId = cancelMsg.StrategyId,
							// при отмене заявки пользовательский идентификатор не меняется
							//UserOrderId = cancelMsg.UserOrderId
						};

						yield break;
					}

					case MessageTypes.OrderPairReplace:
					case MessageTypes.OrderGroupCancel:
						throw new NotSupportedException();

					default:
						throw new ArgumentOutOfRangeException(nameof(message), message.Type, LocalizedStrings.Str1219);
				}
			}

			private void ProcessLevel1(Level1ChangeMessage message, ICollection<Message> result)
			{
				UpdateSecurityDefinition(message);

				var localTime = message.LocalTime;

				if (message.IsContainsTick())
				{
					ProcessTick(message.ToTick(), result);
				}
				else if (message.IsContainsQuotes() && !HasDepth(localTime))
				{
					var prevBidPrice = _l1BidPrice;
					var prevAskPrice = _l1AskPrice;
					var prevBidVol = _l1BidVol;
					var prevAskVol = _l1AskVol;

					_l1BidPrice = (decimal?)message.Changes.TryGetValue(Level1Fields.BestBidPrice) ?? _l1BidPrice;
					_l1AskPrice = (decimal?)message.Changes.TryGetValue(Level1Fields.BestAskPrice) ?? _l1AskPrice;
					_l1BidVol = (decimal?)message.Changes.TryGetValue(Level1Fields.BestBidVolume) ?? _l1BidVol;
					_l1AskVol = (decimal?)message.Changes.TryGetValue(Level1Fields.BestAskVolume) ?? _l1AskVol;

					if (_l1BidPrice == 0)
						_l1BidPrice = null;

					if (_l1AskPrice == 0)
						_l1AskPrice = null;

					if (_l1BidPrice is null && _l1AskPrice is null)
						return;

					if (_l1BidVol == 0)
						_l1BidVol = null;

					if (_l1AskVol == 0)
						_l1AskVol = null;

					if (prevBidPrice == _l1BidPrice && prevAskPrice == _l1AskPrice && prevBidVol == _l1BidVol && prevAskVol == _l1AskVol)
						return;

					void AddQuote(QuotesDict quotes, decimal price, decimal? volume)
						=> quotes.Add(price, new(new(), new() { Price = price, Volume = volume ?? 1 }));

					var serverTime = message.ServerTime;

					if (_activeOrders.Count == 0)
					{
						if (_l1BidPrice is not null)
						{
							foreach (var p in _bids.Values)
							{
								foreach (var quote in p.First)
									_messagePool.Free(quote);
							}

							_bids.Clear();

							AddQuote(_bids, _l1BidPrice.Value, _l1BidVol);
						}

						if (_l1AskPrice is not null)
						{
							foreach (var p in _asks.Values)
							{
								foreach (var quote in p.First)
									_messagePool.Free(quote);
							}

							_asks.Clear();

							AddQuote(_asks, _l1AskPrice.Value, _l1AskVol);
						}
					}
					else
					{
						var bestBid = _bids.FirstOrDefault();
						var bestAsk = _asks.FirstOrDefault();

						void ProcessMarketOrder(QuotesDict quotes, Sides orderSide, decimal price, decimal volume)
						{
							var quotesSide = orderSide.Invert();

#if EMU_DBG
							Verify(quotesSide);
#endif

							var sign = orderSide == Sides.Buy ? -1 : 1;
							var hasQuotes = false;

							List<RefPair<LevelQuotes, QuoteChange>> toRemove = null;

							foreach (var pair in quotes)
							{
								var quote = pair.Value.Second;

								if (quote.Price * sign > price * sign)
								{
									toRemove ??= new();
									toRemove.Add(pair.Value);
								}
								else
								{
									if (quote.Price == price)
									{
										toRemove ??= new();
										toRemove.Add(pair.Value);
									}
									else
									{
										if ((price - quote.Price).Abs() == _securityDefinition.PriceStep)
										{
											// если на один шаг цены выше/ниже есть котировка, то не выполняем никаких действий
											// иначе добавляем новый уровень в стакан, чтобы не было большого расхождения цен.
											hasQuotes = true;
										}

										break;
									}
								}
							}

							if (toRemove is not null)
							{
								var totalVolumeDiff = 0m;

								foreach (var pair in toRemove)
								{
									quotes.Remove(pair.Second.Price);

									totalVolumeDiff += pair.Second.Volume;

									foreach (var quote in pair.First)
									{
										if (quote.PortfolioName is not null)
										{
											if (TryRemoveActiveOrder(quote.TransactionId, out var orderMsg))
											{
												var tradeVol = orderMsg.Balance.Value;

												orderMsg.OriginalTransactionId = quote.TransactionId;
												orderMsg.OrderState = OrderStates.Done;
												orderMsg.Balance = 0;
												result.Add(ToOrder(localTime, orderMsg));

												ProcessOwnTrade(localTime, orderMsg, pair.Second.Price, tradeVol, result);
											}
										}

										_messagePool.Free(quote);
									}
								}

								AddTotalVolume(quotesSide, -totalVolumeDiff);

#if EMU_DBG
								Verify(quotesSide);
#endif
							}

							// если собрали все котировки, то оставляем заявку в стакане по цене котировки
							if (!hasQuotes)
							{
								UpdateQuote(CreateMessage(localTime, serverTime, quotesSide, price, volume), true);

#if EMU_DBG
								Verify(quotesSide);
#endif
							}
						}

						if (bestBid.Value is null)
						{
							if (_l1BidPrice is not null)
								AddQuote(_bids, _l1BidPrice.Value, _l1BidVol);
						}
						else
						{
							if (_l1AskPrice <= bestBid.Key)
								ProcessMarketOrder(_bids, Sides.Sell, _l1AskPrice.Value, _l1AskVol ?? 1);
						}

						if (bestAsk.Value is null)
						{
							if (_l1AskPrice is not null)
								AddQuote(_asks, _l1AskPrice.Value, _l1AskVol);
						}
						else
						{
							if (_l1BidPrice >= bestAsk.Key)
								ProcessMarketOrder(_asks, Sides.Buy, _l1BidPrice.Value, _l1BidVol ?? 1);
						}

						if (_l1BidPrice is not null && !_bids.ContainsKey(_l1BidPrice.Value))
							AddQuote(_bids, _l1BidPrice.Value, _l1BidVol);

						if (_l1AskPrice is not null && !_asks.ContainsKey(_l1AskPrice.Value))
							AddQuote(_asks, _l1AskPrice.Value, _l1AskVol);

						CancelWorst(localTime, serverTime);
					}

					if (_depthSubscription is not null)
					{
						result.Add(CreateQuoteMessage(
							message.SecurityId,
							localTime,
							serverTime));
					}
				}

				if (_l1Subscription is not null)
					result.Add(message);
			}

			private void ProcessQuoteChange(QuoteChangeMessage message, ICollection<Message> result)
			{
				var localTime = message.LocalTime;
				var serverTime = message.ServerTime;

				if (message.State is null)
				{
					if (_booksWithState is null)
						_booksWithState = false;
					else if (_booksWithState == true)
						return;

					if (!_priceStepUpdated || !_volumeStepUpdated)
					{
						var quote = message.GetBestBid() ?? message.GetBestAsk();

						if (quote != null)
							UpdateSteps(quote.Value.Price, quote.Value.Volume);
					}

					_lastDepthDate = message.LocalTime.Date;

					if (_activeOrders.Count == 0)
					{
						foreach (var p in _bids.Values)
						{
							foreach (var quote in p.First)
								_messagePool.Free(quote);
						}

						foreach (var p in _asks.Values)
						{
							foreach (var quote in p.First)
								_messagePool.Free(quote);
						}

						_bids.Clear();
						_asks.Clear();

						decimal? bestBidPrice = null;
						decimal? bestAskPrice = null;

						foreach (var bid in message.Bids)
						{
							bestBidPrice ??= bid.Price;
							_bids.Add(bid.Price, new(new(), new() { Price = bid.Price, Volume = bid.Volume }));
						}

						foreach (var ask in message.Asks)
						{
							bestAskPrice ??= ask.Price;
							_asks.Add(ask.Price, new(new(), new() { Price = ask.Price, Volume = ask.Volume }));
						}

						_currSpreadPrice = bestBidPrice.GetSpreadMiddle(bestAskPrice) ?? 0;
					}
					else
					{
						var diff = new List<ExecutionMessage>();

						decimal GetDiff(QuotesDict from, QuoteChange[] to, Sides side)
						{
							void AddExecMsg(QuoteChange quote, decimal volume, Sides side, bool isSpread)
							{
								if (volume > 0)
									diff.Add(CreateMessage(localTime, serverTime, side, quote.Price, volume));
								else
								{
									volume = volume.Abs();

									// matching only top orders (spread)
									if (isSpread && volume > 1 && _isMatch.Next())
									{
										var tradeVolume = (int)volume / 2;

										diff.Add(new ExecutionMessage
										{
											Side = side,
											TradeVolume = tradeVolume,
											DataTypeEx = DataType.Ticks,
											SecurityId = _securityId,
											LocalTime = localTime,
											ServerTime = serverTime,
											TradePrice = quote.Price,
										});

										// that tick will not affect on order book
										//volume -= tradeVolume;
									}

									diff.Add(CreateMessage(localTime, serverTime, side, quote.Price, volume, true));
								}
							}

							var newBestPrice = 0m;

							var canProcessFrom = true;
							var canProcessTo = true;

							QuoteChange? currFrom = null;
							QuoteChange? currTo = null;

							var mult = side == Sides.Buy ? -1 : 1;
							bool? isSpread = null;

							using var fromEnum = from.GetEnumerator();
							using var toEnum = ((IEnumerable<QuoteChange>)to).GetEnumerator();

							while (true)
							{
								if (canProcessFrom && currFrom == null)
								{
									if (!fromEnum.MoveNext())
										canProcessFrom = false;
									else
									{
										currFrom = fromEnum.Current.Value.Second;
										isSpread = isSpread == null;
									}
								}

								if (canProcessTo && currTo == null)
								{
									if (!toEnum.MoveNext())
										canProcessTo = false;
									else
									{
										currTo = toEnum.Current;

										if (newBestPrice == 0)
											newBestPrice = currTo.Value.Price;
									}
								}

								if (currFrom == null)
								{
									if (currTo == null)
										break;
									else
									{
										var v = currTo.Value;

										AddExecMsg(v, v.Volume, side, false);
										currTo = null;
									}
								}
								else
								{
									if (currTo == null)
									{
										var v = currFrom.Value;
										AddExecMsg(v, -v.Volume, side, isSpread.Value);
										currFrom = null;
									}
									else
									{
										var f = currFrom.Value;
										var t = currTo.Value;

										if (f.Price == t.Price)
										{
											if (f.Volume != t.Volume)
											{
												AddExecMsg(t, t.Volume - f.Volume, side, isSpread.Value);
											}

											currFrom = currTo = null;
										}
										else if (f.Price * mult > t.Price * mult)
										{
											AddExecMsg(t, t.Volume, side, isSpread.Value);
											currTo = null;
										}
										else
										{
											AddExecMsg(f, -f.Volume, side, isSpread.Value);
											currFrom = null;
										}
									}
								}
							}

							return newBestPrice;
						}

						var bestBidPrice = GetDiff(_bids, message.Bids, Sides.Buy);
						var bestAskPrice = GetDiff(_asks, message.Asks, Sides.Sell);

						var spreadPrice = bestAskPrice == 0
							? bestBidPrice
							: (bestBidPrice == 0
								? bestAskPrice
								: (bestAskPrice - bestBidPrice) / 2 + bestBidPrice);

						//при обновлении стакана необходимо учитывать направление сдвига, чтобы не было ложного исполнения при наложении бидов и асков.
						//т.е. если цена сдвинулась вниз, то обновление стакана необходимо начинать с минимального бида.
						var diffs = (spreadPrice < _currSpreadPrice)
								? diff.OrderBy(m => m.OrderPrice)
								: diff.OrderByDescending(m => m.OrderPrice);

						foreach (var m in diffs)
						{
							if (m.DataType == DataType.Ticks)
							{
								m.ServerTime = message.ServerTime;
								result.Add(m);
							}
							else
								Process(m, result);
						}

						_currSpreadPrice = spreadPrice;
					}
				}
				else
				{
					if (_booksWithState is null)
						_booksWithState = true;
					else if (_booksWithState == false)
						return;

					if (message.HasPositions)
						throw new NotSupportedException("Order books with positions not supported.");

					var canApply = false;

					switch (message.State)
					{
						case QuoteChangeStates.SnapshotStarted:
						case QuoteChangeStates.SnapshotBuilding:
						case QuoteChangeStates.SnapshotComplete:
						{
							_bookBuilder ??= new(_securityId);

							canApply = message.State == QuoteChangeStates.SnapshotComplete;
							message = _bookBuilder.TryApply(message);

							if (canApply)
								_bookBuilder = null;

							break;
						}
						case QuoteChangeStates.Increment:
							if (_bookBuilder is not null)
								return;

							canApply = true;
							break;
						default:
							throw new ArgumentOutOfRangeException(message.State.To<string>());
					}

					if (canApply)
					{
						var isIncremental = message.State == QuoteChangeStates.Increment;

						void Apply(QuotesDict quotes, QuoteChange[] newState)
						{
							var toRemove = isIncremental ? null : quotes.Keys.ToHashSet();

							void RemoveLevel(decimal price)
							{
								if (!quotes.TryGetAndRemove(price, out var p))
									return;

								foreach (var quote in p.First)
								{
									_messagePool.Free(quote);

									if (!TryRemoveActiveOrder(quote.TransactionId, out var orderMsg))
										continue;

									var balance = orderMsg.Balance.Value;

									orderMsg.Balance = 0;
									orderMsg.OrderState = OrderStates.Done;
									result.Add(ToOrder(localTime, orderMsg));

									ProcessOwnTrade(localTime, orderMsg, price, balance, result);
								}
							}

							foreach (var newQuote in newState)
							{
								var price = newQuote.Price;
								var volume = newQuote.Volume;

								if (isIncremental && volume == 0)
								{
									RemoveLevel(price);
									continue;
								}

								toRemove?.Remove(price);

								if (quotes.TryGetValue(price, out var p))
								{
									var quote = p.Second;

									var diff = quote.Volume - volume;

									// old volume is more than new
									if (diff > 0)
									{
										foreach (var lq in p.First)
										{
											if (!TryRemoveActiveOrder(lq.TransactionId, out var orderMsg))
												continue;

											var balance = orderMsg.Balance.Value;

											if (balance <= diff)
											{
												orderMsg.Balance = 0;
												orderMsg.OrderState = OrderStates.Done;
											}
											else
											{
												orderMsg.Balance -= diff;
											}

											result.Add(ToOrder(localTime, orderMsg));

											ProcessOwnTrade(localTime, orderMsg, price, balance, result);

											diff -= balance;

											if (diff <= 0)
												break;
										}
									}

									quote.Volume = volume;
									p.Second = quote;
								}
								else
								{
									quotes.Add(price, RefTuple.Create(new LevelQuotes(), newQuote));
								}
							}

							if (toRemove is not null)
							{
								foreach (var price in toRemove)
									RemoveLevel(price);
							}
						}

						Apply(_bids, message.Bids);
						Apply(_asks, message.Asks);
					}
				}

				if (_depthSubscription is not null)
				{
					// возращаем не входящий стакан, а тот, что сейчас хранится внутри эмулятора.
					// таким образом мы можем видеть в стакане свои цены и объемы

					result.Add(CreateQuoteMessage(
						message.SecurityId,
						localTime,
						serverTime));
				}
			}

			private void ProcessTick(ExecutionMessage tick, ICollection<Message> result)
			{
				ProcessTick(tick.SecurityId, tick.LocalTime, tick.ServerTime, (tick.OriginSide, tick.GetTradePrice(), tick.TradeVolume ?? 1), result);
			}

			private void AddActiveOrder(ExecutionMessage orderMsg, DateTimeOffset time)
			{
				_activeOrders.Add(orderMsg.TransactionId, orderMsg);

				if (orderMsg.ExpiryDate != null)
					_expirableOrders.Add(orderMsg, orderMsg.ExpiryDate.Value.EndOfDay() - time);
			}

			private bool TryRemoveActiveOrder(long transId, out ExecutionMessage orderMsg)
			{
				if (!_activeOrders.TryGetAndRemove(transId, out orderMsg))
					return false;

				_expirableOrders.Remove(orderMsg);
				return true;
			}

			private void ProcessTick(SecurityId secId, DateTimeOffset localTime, DateTimeOffset serverTime, (Sides? side, decimal price, decimal volume) tick, ICollection<Message> result)
			{
				var tradePrice = tick.price;
				var tradeVolume = tick.volume;
				var side = tick.side;

				UpdateSteps(tradePrice, tradeVolume);

				var bestBid = _bids.FirstOrDefault();
				var bestAsk = _asks.FirstOrDefault();

				var hasDepth = HasDepth(localTime);

				void TryCreateOppositeOrder(Sides originSide)
				{
					var quotesSide = originSide.Invert();
					var quotes = GetQuotes(quotesSide);

					var priceStep = GetPriceStep();
					var oppositePrice = (tradePrice + _settings.SpreadSize * priceStep * (originSide == Sides.Buy ? 1 : -1)).Max(priceStep);

					var bestQuote = quotes.FirstOrDefault();

					if (bestQuote.Value == null || ((originSide == Sides.Buy && oppositePrice < bestQuote.Key) || (originSide == Sides.Sell && oppositePrice > bestQuote.Key)))
					{
						UpdateQuote(CreateMessage(localTime, serverTime, quotesSide, oppositePrice, tradeVolume), true);

#if EMU_DBG
						Verify(quotesSide);
#endif
					}
				}

				Sides GetOrderSide()
				{
					return side is null
						? tradePrice > _prevTickPrice ? Sides.Sell : Sides.Buy
						: side.Value.Invert();
				}

				void ProcessMarketOrder(Sides orderSide)
				{
					var quotesSide = orderSide.Invert();
					var quotes = GetQuotes(quotesSide);

#if EMU_DBG
					Verify(quotesSide);
#endif

					var sign = orderSide == Sides.Buy ? -1 : 1;
					var hasQuotes = false;

					List<RefPair<LevelQuotes, QuoteChange>> toRemove = null;

					foreach (var pair in quotes)
					{
						var quote = pair.Value.Second;

						if (quote.Price * sign > tradePrice * sign)
						{
							toRemove ??= new();
							toRemove.Add(pair.Value);
						}
						else
						{
							if (quote.Price == tradePrice)
							{
								toRemove ??= new();
								toRemove.Add(pair.Value);
							}
							else
							{
								if ((tradePrice - quote.Price).Abs() == _securityDefinition.PriceStep)
								{
									// если на один шаг цены выше/ниже есть котировка, то не выполняем никаких действий
									// иначе добавляем новый уровень в стакан, чтобы не было большого расхождения цен.
									hasQuotes = true;
								}

								break;
							}
						}
					}

					if (toRemove is not null)
					{
						var totalVolumeDiff = 0m;

						foreach (var pair in toRemove)
						{
							quotes.Remove(pair.Second.Price);

							totalVolumeDiff += pair.Second.Volume;

							foreach (var quote in pair.First)
							{
								if (quote.PortfolioName is not null)
								{
									if (TryRemoveActiveOrder(quote.TransactionId, out var orderMsg))
									{
										var tradeVol = orderMsg.Balance.Value;

										orderMsg.OriginalTransactionId = quote.TransactionId;
										orderMsg.OrderState = OrderStates.Done;
										orderMsg.Balance = 0;
										result.Add(ToOrder(localTime, orderMsg));

										ProcessOwnTrade(localTime, orderMsg, pair.Second.Price, tradeVol, result);
									}
								}

								_messagePool.Free(quote);
							}
						}

						AddTotalVolume(quotesSide, -totalVolumeDiff);

#if EMU_DBG
						Verify(quotesSide);
#endif
					}

					// если собрали все котировки, то оставляем заявку в стакане по цене сделки
					if (!hasQuotes)
					{
						UpdateQuote(CreateMessage(localTime, serverTime, quotesSide, tradePrice, tradeVolume), true);

#if EMU_DBG
						Verify(quotesSide);
#endif
					}
				}

				if (bestBid.Value is not null && tradePrice <= bestBid.Key)
				{
					// тик попал в биды, значит была крупная заявка по рынку на продажу,
					// которая возможна исполнила наши заявки

					ProcessMarketOrder(Sides.Sell);

					if (!hasDepth)
					{
						// подтягиваем противоположные котировки и снимаем лишние заявки
						TryCreateOppositeOrder(Sides.Buy);
					}
				}
				else if (bestAsk.Value is not null && tradePrice >= bestAsk.Key)
				{
					// тик попал в аски, значит была крупная заявка по рынку на покупку,
					// которая возможна исполнила наши заявки

					ProcessMarketOrder(Sides.Buy);

					if (!hasDepth)
					{
						// подтягиваем противоположные котировки и снимаем лишние заявки
						TryCreateOppositeOrder(Sides.Sell);
					}
				}
				else if (bestBid.Value is not null && bestAsk.Value is not null && bestBid.Key < tradePrice && tradePrice < bestAsk.Key)
				{
					// тик попал в спред, значит в спреде до сделки была заявка.
					// создаем две лимитки с разных сторон, но одинаковой ценой.
					// если в эмуляторе есть наша заявка на этом уровне, то она исполниться.
					// если нет, то эмулятор взаимно исполнит эти заявки друг об друга

					// [upd] 2023/2/13 - не понятно как наша заявка может оказаться на этом уровне
					// если тик попал в середину спреда (и значит на уровне нет ни наших, ни сгенерированных заявок)

					var originSide = GetOrderSide();
					var spreadStep = _settings.SpreadSize * GetPriceStep();

					// try to fill depth gaps

					var newBestPrice = tradePrice + spreadStep;

					var depth = _settings.MaxDepth;
					while (--depth > 0)
					{
						if (bestAsk.Key > newBestPrice)
						{
							UpdateQuote(CreateMessage(localTime, serverTime, Sides.Sell, newBestPrice, _volumeRandom.Next(10, 100)), true);
							newBestPrice += spreadStep * _priceRandom.Next(1, _settings.SpreadSize);
						}
						else
							break;
					}

					newBestPrice = tradePrice - spreadStep;

					depth = _settings.MaxDepth;
					while (--depth > 0)
					{
						if (newBestPrice > bestBid.Key)
						{
							UpdateQuote(CreateMessage(localTime, serverTime, Sides.Buy, newBestPrice, _volumeRandom.Next(10, 100)), true);
							newBestPrice -= spreadStep * _priceRandom.Next(1, _settings.SpreadSize);
						}
						else
							break;
					}
				}
				else
				{
					// если у нас стакан был полу пустой, то тик формирует некий ценовой уровень в стакана,
					// так как прошедщая заявка должна была обо что-то удариться. допускаем, что после
					// прохождения сделки на этом ценовом уровне остался объем равный тиковой сделки

					var hasOpposite = true;

					Sides originSide;

					// определяем направление псевдо-ранее существовавшей заявки, из которой получился тик
					if (bestBid.Value != null)
						originSide = Sides.Sell;
					else if (bestAsk.Value != null)
						originSide = Sides.Buy;
					else
					{
						originSide = GetOrderSide();
						hasOpposite = false;
					}

					UpdateQuote(CreateMessage(localTime, serverTime, originSide, tradePrice, tradeVolume), true);

					// если стакан был полностью пустой, то формируем сразу уровень с противоположной стороны
					if (!hasOpposite)
					{
						var oppositePrice = tradePrice + _settings.SpreadSize * GetPriceStep() * (originSide == Sides.Buy ? 1 : -1);

						if (oppositePrice > 0)
							UpdateQuote(CreateMessage(localTime, serverTime, originSide.Invert(), oppositePrice, tradeVolume), true);
					}
				}

				if (!hasDepth)
				{
					CancelWorst(localTime, serverTime);
				}

				_prevTickPrice = tradePrice;

				if (_ticksSubscription is not null)
				{
					result.Add(tick.ToTickMessage(secId, localTime, serverTime));
				}

				if (_depthSubscription is not null)
				{
					result.Add(CreateQuoteMessage(
						secId,
						localTime,
						serverTime));
				}
			}

			private void CancelWorst(DateTimeOffset localTime, DateTimeOffset serverTime)
			{
				void CancelWorstQuote(Sides side)
				{
					var quotes = GetQuotes(side);

					if (quotes.Count <= _settings.MaxDepth)
						return;

					var worst = quotes.Last();
					var volume = worst.Value.First.Where(e => e.PortfolioName == null).Sum(e => e.OrderVolume.Value);

					if (volume == 0)
						return;

					UpdateQuote(CreateMessage(localTime, serverTime, side, worst.Key, volume, true), false);
				}

				// если стакан слишком разросся, то удаляем его хвосты (не удаляя пользовательские заявки)
				CancelWorstQuote(Sides.Buy);
				CancelWorstQuote(Sides.Sell);
			}

			private decimal GetPriceStep() => _securityDefinition?.PriceStep ?? 0.01m;
			private bool HasDepth(DateTimeOffset time) => _lastDepthDate == time.Date;

			private void UpdateSteps(decimal price, decimal? volume)
			{
				if (!_priceStepUpdated)
				{
					_securityDefinition.PriceStep = price.GetDecimalInfo().EffectiveScale.GetPriceStep();
					_priceStepUpdated = true;
				}

				if (!_volumeStepUpdated)
				{
					if (volume != null)
					{
						_securityDefinition.VolumeStep = volume.Value.GetDecimalInfo().EffectiveScale.GetPriceStep();
						_volumeStepUpdated = true;
					}
				}
			}

			private void UpdateSecurityDefinition(SecurityMessage securityDefinition)
			{
				_securityDefinition = securityDefinition ?? throw new ArgumentNullException(nameof(securityDefinition));

				if (_securityDefinition.PriceStep != null)
				{
					_priceStepUpdated = true;
					_priceStepExplicit = true;
				}

				if (_securityDefinition.VolumeStep != null)
				{
					_volumeStepUpdated = true;
					_priceStepExplicit = true;
				}
			}

			private void UpdatePriceLimits(ExecutionMessage execution, ICollection<Message> result)
			{
				if (_lastStripDate == execution.LocalTime.Date)
					return;

				decimal price;

				if (execution.DataType == DataType.Ticks)
					price = execution.GetTradePrice();
				else if (execution.DataType == DataType.OrderLog)
				{
					if (execution.TradePrice == null)
						return;

					price = execution.TradePrice.Value;
				}
				else
					return;

				_lastStripDate = execution.LocalTime.Date;

				var priceOffset = _settings.PriceLimitOffset;
				var priceStep = GetPriceStep();

				var level1Msg =
					new Level1ChangeMessage
					{
						SecurityId = execution.SecurityId,
						LocalTime = execution.LocalTime,
						ServerTime = execution.ServerTime,
					}
					.Add(Level1Fields.MinPrice, ShrinkPrice((decimal)(price - priceOffset), priceStep))
					.Add(Level1Fields.MaxPrice, ShrinkPrice((decimal)(price + priceOffset), priceStep));

				_parent.UpdateLevel1Info(level1Msg, result, true);
			}

			private void UpdateSecurityDefinition(Level1ChangeMessage message)
			{
				if (_securityDefinition == null)
					return;

				foreach (var change in message.Changes)
				{
					switch (change.Key)
					{
						case Level1Fields.PriceStep:
							_securityDefinition.PriceStep = (decimal)change.Value;
							// при изменении шага надо пересчитать планки
							_lastStripDate = DateTime.MinValue;
							_priceStepUpdated = true;
							_priceStepExplicit = true;
							break;
						case Level1Fields.VolumeStep:
							_securityDefinition.VolumeStep = (decimal)change.Value;
							_volumeDecimals = GetVolumeStep().GetCachedDecimals();
							_volumeStepUpdated = true;
							_volumeStepExplicit = true;
							break;
						case Level1Fields.MinVolume:
							_securityDefinition.MinVolume = (decimal)change.Value;
							break;
						case Level1Fields.MaxVolume:
							_securityDefinition.MaxVolume = (decimal)change.Value;
							break;
						case Level1Fields.Multiplier:
							_securityDefinition.Multiplier = (decimal)change.Value;
							break;
					}
				}

				UpdateSecurityDefinition(_securityDefinition);
			}

			private decimal GetVolumeStep()
			{
				return _securityDefinition.VolumeStep ?? 1;
			}

			private static decimal ShrinkPrice(decimal price, decimal priceStep)
			{
				var decimals = priceStep.GetCachedDecimals();

				return price
					.Round(priceStep, decimals)
					.RemoveTrailingZeros();
			}

			private static ExecutionMessage CreateReply(ExecutionMessage original, DateTimeOffset time, Exception error)
			{
				var replyMsg = new ExecutionMessage
				{
					HasOrderInfo = true,
					DataTypeEx = DataType.Transactions,
					ServerTime = time,
					LocalTime = time,
					OriginalTransactionId = original.TransactionId,
					Error = error,
					StrategyId = original.StrategyId
				};

				if (error != null)
					replyMsg.OrderState = OrderStates.Failed;

				return replyMsg;
			}

			private void AcceptExecution(DateTimeOffset time, ExecutionMessage execution, ICollection<Message> result)
			{
				if (_settings.Failing > 0)
				{
					if (RandomGen.GetDouble() < (_settings.Failing / 100.0))
					{
						this.AddErrorLog(LocalizedStrings.Str1151Params, execution.IsCancellation ? LocalizedStrings.Str1152 : LocalizedStrings.Str1153, execution.OriginalTransactionId == 0 ? execution.TransactionId : execution.OriginalTransactionId);

						var replyMsg = CreateReply(execution, time, new InvalidOperationException(LocalizedStrings.Str1154));

						replyMsg.Balance = execution.OrderVolume;

						result.Add(replyMsg);
						return;
					}
				}

				if (execution.IsCancellation)
				{
					if (TryRemoveActiveOrder(execution.OriginalTransactionId, out var order))
					{
						// изменяем текущие котировки, добавляя туда наши цену и объем
						UpdateQuote(order, false);

						if (_depthSubscription is not null)
						{
							// отправляем измененный стакан
							result.Add(CreateQuoteMessage(
								order.SecurityId,
								time,
								GetServerTime(time)));
						}

						var replyMsg = CreateReply(order, time, null);

						//replyMsg.OriginalTransactionId = execution.OriginalTransactionId;
						replyMsg.OrderState = OrderStates.Done;

						result.Add(replyMsg);

						this.AddInfoLog(LocalizedStrings.Str1155Params, execution.OriginalTransactionId);

						replyMsg.Commission = _parent
							.GetPortfolioInfo(execution.PortfolioName)
							.ProcessOrder(order, order.Balance.Value, result);
					}
					else
					{
						result.Add(CreateReply(execution, time, new InvalidOperationException(LocalizedStrings.Str1156Params.Put(execution.OriginalTransactionId))));

						this.AddErrorLog(LocalizedStrings.Str1156Params, execution.OriginalTransactionId);
					}
				}
				else
				{
					var error = _parent.CheckRegistration(execution, _securityDefinition, _priceStepExplicit, _volumeStepExplicit/*, result*/);

					var replyMsg = CreateReply(execution, time, error);
					result.Add(replyMsg);

					if (error is null)
					{
						this.AddInfoLog(LocalizedStrings.Str1157Params, execution.TransactionId);

						// при восстановлении заявки у нее уже есть номер
						if (execution.OrderId == null)
						{
							execution.Balance = execution.OrderVolume;
							execution.OrderState = OrderStates.Active;
							execution.OrderId = _parent.OrderIdGenerator.GetNextId();
						}
						else
							execution.ServerTime = execution.ServerTime; // при восстановлении не меняем время

						replyMsg.Commission = _parent
							.GetPortfolioInfo(execution.PortfolioName)
							.ProcessOrder(execution, null, result);

#if EMU_DBG
						Verify(Sides.Buy);
						Verify(Sides.Sell);
#endif

						MatchOrder(execution.LocalTime, execution, result, true);

#if EMU_DBG
						Verify(Sides.Buy);
						Verify(Sides.Sell);
#endif

						if (execution.OrderState == OrderStates.Active)
						{
							replyMsg.OrderState = OrderStates.Active;

							AddActiveOrder(execution, time);

							// изменяем текущие котировки, добавляя туда наши цену и объем
							UpdateQuote(execution, true);
						}
						else if (execution.IsCanceled())
						{
							_parent
								.GetPortfolioInfo(execution.PortfolioName)
								.ProcessOrder(execution, execution.Balance.Value, result);
						}

						if (_depthSubscription is not null)
						{
							// отправляем измененный стакан
							result.Add(CreateQuoteMessage(
								execution.SecurityId,
								time,
								GetServerTime(time)));
						}
					}
					else
					{
						this.AddInfoLog(LocalizedStrings.Str1158Params, execution.TransactionId, error.Message);
					}
				}
			}

			private QuoteChangeMessage CreateQuoteMessage(SecurityId securityId, DateTimeOffset timeStamp, DateTimeOffset time)
			{
				return new QuoteChangeMessage
				{
					SecurityId = securityId,
					LocalTime = timeStamp,
					ServerTime = time,
					Bids = BuildQuoteChanges(_bids),
					Asks = BuildQuoteChanges(_asks),
				};
			}

			private static QuoteChange[] BuildQuoteChanges(QuotesDict quotes)
			{
				return quotes.Count == 0
					? Array.Empty<QuoteChange>()
					: quotes.Select(p => p.Value.Second).ToArray();
			}

			private void UpdateQuotes(ExecutionMessage message, ICollection<Message> result)
			{
				// матчинг заявок происходит не только для своих сделок, но и для чужих.
				// различие лишь в том, что для чужих заявок не транслируется информация о сделках.
				// матчинг чужих заявок на равне со своими дает наиболее реалистичный сценарий обновления стакана.

				if (message.TradeId != null)
					throw new ArgumentException(LocalizedStrings.Str1159, nameof(message));

				if (message.OrderVolume is null or <= 0)
					throw new ArgumentOutOfRangeException(nameof(message), message.OrderVolume, LocalizedStrings.Str1160Params.Put(message.TransactionId));

				if (message.IsCancellation)
				{
					UpdateQuote(message, false);
					return;
				}

				// не ставим чужую заявку в стакан сразу, только её остаток после матчинга
				//UpdateQuote(message, true);

				if (_activeOrders.Count > 0)
				{
					foreach (var order in _activeOrders.Values.ToArray())
					{
						MatchOrder(message.LocalTime, order, result, false);

						if (order.OrderState != OrderStates.Done)
							continue;

						TryRemoveActiveOrder(order.TransactionId, out _);

						// изменяем текущие котировки, удаляя оттуда наши цену и объем
						UpdateQuote(order, false);
					}
				}

				//для чужих FOK заявок необходимо убрать ее из стакана после исполнения своих заявок
				// [upd] теперь не ставим чужую заявку сразу в стакан, поэтому и удалять не нужно
				//if (message.TimeInForce == TimeInForce.MatchOrCancel && !message.IsCancelled)
				//{
				//	UpdateQuote(new ExecutionMessage
				//	{
				//		DataTypeEx = DataType.Transactions,
				//		Side = message.Side,
				//		OrderPrice = message.OrderPrice,
				//		OrderVolume = message.OrderVolume,
				//		HasOrderInfo = true,
				//	}, false);
				//}

				// для чужих заявок заполняется только объем
				message.Balance = message.OrderVolume;

				// исполняем чужую заявку как свою. при этом результат выполнения не идет никуда
				MatchOrder(message.LocalTime, message, null, true);

				if (message.Balance > 0)
				{
					UpdateQuote(message, true, false);
				}
			}

			private QuotesDict GetQuotes(Sides side)
			{
				return side switch
				{
					Sides.Buy => _bids,
					Sides.Sell => _asks,
					_ => throw new ArgumentOutOfRangeException(nameof(side), side, LocalizedStrings.Str1219),
				};
			}

			private void MatchOrder(DateTimeOffset time, ExecutionMessage order, ICollection<Message> result, bool isNewOrder)
			{
				//string matchError = null;
				var isCrossTrade = false;

				var executions = result == null ? null : new Dictionary<decimal, decimal>();

				var quotesSide = order.Side.Invert();
				var quotes = GetQuotes(quotesSide);

				List<decimal> toRemove = null;

				var leftBalance = order.GetBalance();
				var sign = order.Side == Sides.Buy ? 1 : -1;
				var orderPrice = order.OrderPrice;
				var isMarket = order.OrderType == OrderTypes.Market;

				foreach (var pair in quotes)
				{
					var price = pair.Key;
					var levelQuotes = pair.Value.First;
					var qc = pair.Value.Second;

					// для старых заявок, когда стакан пробивает уровень заявки,
					// матчим по цене ранее выставленной заявки.
					var execPrice = isNewOrder ? price : orderPrice;

					if (!isMarket)
					{
						if (sign * price > sign * orderPrice)
							break;

						if (price == orderPrice && !_settings.MatchOnTouch)
							break;
					}

					// объем заявки больше или равен всему уровню в стакане, то сразу удаляем его целиком
					if (leftBalance >= qc.Volume)
					{
						if (executions != null)
						{
							for (var i = 0; i < levelQuotes.Count; i++)
							{
								var quote = levelQuotes[i];

								// если это пользовательская заявка и матчинг идет о заявку с таким же портфелем
								if (quote.PortfolioName == order.PortfolioName)
								{
									var matchError = LocalizedStrings.Str1161Params.Put(quote.TransactionId, order.TransactionId);
									this.AddErrorLog(matchError);

									isCrossTrade = true;
									break;
								}

								var volume = quote.GetBalance().Min(leftBalance);

								if (volume <= 0)
									throw new InvalidOperationException(LocalizedStrings.Str1162);

								executions[execPrice] = executions.TryGetValue(execPrice) + volume;
								this.AddInfoLog(LocalizedStrings.Str1163Params, order.TransactionId, volume, execPrice);

								levelQuotes.RemoveAt(i, quote);
								_messagePool.Free(quote);

#if EMU_DBG
								Verify(quotesSide);
#endif
							}
						}
						else
						{
							toRemove ??= new List<decimal>();

							toRemove.Add(price);

							foreach (var quote in levelQuotes)
								_messagePool.Free(quote);

							AddTotalVolume(quotesSide, -qc.Volume);
						}

						leftBalance -= qc.Volume;
					}
					else
					{
						for (var i = 0; i < levelQuotes.Count; i++)
						{
							var quote = levelQuotes[i];

							// если это пользовательская заявка и матчинг идет о заявку с таким же портфелем
							if (executions != null && quote.PortfolioName == order.PortfolioName)
							{
								var matchError = LocalizedStrings.Str1161Params.Put(quote.TransactionId, order.TransactionId);
								this.AddErrorLog(matchError);

								isCrossTrade = true;
								break;
							}

							var volume = quote.GetBalance().Min(leftBalance);

							if (volume <= 0)
								throw new InvalidOperationException(LocalizedStrings.Str1162);

							// если это пользовательская заявка
							if (executions != null)
							{
								executions[execPrice] = executions.TryGetValue(execPrice) + volume;
								this.AddInfoLog(LocalizedStrings.Str1163Params, order.TransactionId, volume, execPrice);
							}

							quote.Balance -= volume;

							if (quote.Balance == 0)
							{
								levelQuotes.RemoveAt(i, quote);
								i--;

								_messagePool.Free(quote);

								if (levelQuotes.Count == 0)
								{
									toRemove ??= new List<decimal>();

									toRemove.Add(price);
								}
							}

							AddTotalVolume(quotesSide, -volume);
							qc.Volume -= volume;
							leftBalance -= volume;

							pair.Value.Second = qc;

#if EMU_DBG
							Verify(quotesSide);
#endif

							if (leftBalance == 0)
								break;
						}
					}

					if (leftBalance == 0 || isCrossTrade)
						break;
				}

				if (toRemove != null)
				{
					foreach (var value in toRemove)
						quotes.Remove(value);

#if EMU_DBG
					Verify(quotesSide);
#endif
				}

				// если это не пользовательская заявка
				if (result == null)
				{
					order.Balance = leftBalance;
					return;
				}

				leftBalance = order.GetBalance() - executions.Values.Sum();

				switch (order.TimeInForce)
				{
					case null:
					case TimeInForce.PutInQueue:
					{
						order.Balance = leftBalance;

						if (executions.Count > 0)
						{
							if (leftBalance == 0)
							{
								order.OrderState = OrderStates.Done;
								this.AddInfoLog(LocalizedStrings.Str1164Params, order.TransactionId);
							}

							result.Add(ToOrder(time, order));
						}

						if (isMarket)
						{
							if (leftBalance > 0)
							{
								this.AddInfoLog(LocalizedStrings.Str1165Params, order.TransactionId, leftBalance);

								order.OrderState = OrderStates.Done;
								result.Add(ToOrder(time, order));
							}
						}

						break;
					}

					case TimeInForce.MatchOrCancel:
					{
						if (leftBalance == 0)
							order.Balance = 0;

						this.AddInfoLog(LocalizedStrings.Str1166Params, order.TransactionId);

						order.OrderState = OrderStates.Done;
						result.Add(ToOrder(time, order));

						// заявка не исполнилась полностью, поэтому она вся отменяется, не влияя на стакан
						if (leftBalance > 0)
							return;

						break;
					}

					case TimeInForce.CancelBalance:
					{
						this.AddInfoLog(LocalizedStrings.Str1167Params, order.TransactionId);

						order.Balance = leftBalance;
						order.OrderState = OrderStates.Done;
						result.Add(ToOrder(time, order));
						break;
					}
				}

				if (isCrossTrade)
				{
					var reply = CreateReply(order, time, null);

					//reply.OrderState = OrderStates.Failed;
					//reply.OrderStatus = (long?)OrderStatus.RejectedBySystem;
					//reply.Error = new InvalidOperationException(matchError);

					reply.OrderState = OrderStates.Done;
					//reply.OrderStatus = (long?)OrderStatus.CanceledByManager;

					result.Add(reply);
				}

				foreach (var execution in executions)
				{
					ProcessOwnTrade(time, order, execution.Key, execution.Value, result);
				}
			}

			private void ProcessTime(Message message, ICollection<Message> result)
			{
				ProcessExpirableOrders(message, result);
				ProcessPendingExecutions(message, result);
				ProcessCandleTrades(message, result);
			}

			private void ProcessCandleTrades(Message message, ICollection<Message> result)
			{
				if (_candleInfo.Count == 0)
					return;

				List<DateTimeOffset> toRemove = null;

				foreach (var pair in _candleInfo)
				{
					if (pair.Key < message.LocalTime)
					{
						toRemove ??= new();
						toRemove.Add(pair.Key);

						if (_ticksSubscription is not null)
						{
							foreach (var (candle, ticks) in pair.Value)
							{
								for (var i = 1; i < ticks.Length; i++)
								{
									var trade = ticks[i];

									if (trade == default)
										break;

									result.Add(trade.ToTickMessage(candle.SecurityId, candle.LocalTime, candle.OpenTime));
								}
							}
						}

						// change current time before the candle will be processed
						result.Add(new TimeMessage { LocalTime = message.LocalTime });

						foreach (var (candle, ticks) in pair.Value)
						{
							candle.LocalTime = message.LocalTime;
							result.Add(candle);

							_tickPool.Free(ticks);
						}
					}
				}

				if (toRemove is not null)
				{
					foreach (var key in toRemove)
					{
						_candleInfo.Remove(key);
					}
				}
			}

			private void ProcessExpirableOrders(Message message, ICollection<Message> result)
			{
				if (_expirableOrders.Count == 0)
					return;

				var diff = message.LocalTime - _prevTime;

				foreach (var pair in _expirableOrders.ToArray())
				{
					var orderMsg = pair.Key;
					var left = pair.Value;
					left -= diff;

					if (left <= TimeSpan.Zero)
					{
						TryRemoveActiveOrder(orderMsg.TransactionId, out _);

						orderMsg.OrderState = OrderStates.Done;
						result.Add(ToOrder(message.LocalTime, orderMsg));

						// изменяем текущие котировки, удаляя оттуда наши цену и объем
						UpdateQuote(orderMsg, false);

						if (_depthSubscription is not null)
						{
							// отправляем измененный стакан
							result.Add(CreateQuoteMessage(
								orderMsg.SecurityId,
								message.LocalTime,
								GetServerTime(message.LocalTime)));
						}
					}
					else
						_expirableOrders[orderMsg] = left;
				}
			}

			private void UpdateQuote(ExecutionMessage message, bool register, bool byVolume = true)
			{
				var quotes = GetQuotes(message.Side);

				var pair = quotes.TryGetValue(message.OrderPrice);

				if (pair == null)
				{
					if (!register)
						return;

					quotes[message.OrderPrice] = pair = RefTuple.Create(new LevelQuotes(), new QuoteChange(message.OrderPrice, 0));
				}

				var level = pair.First;

				var volume = byVolume ? message.SafeGetVolume() : message.GetBalance();

				if (register)
				{
					//если пришло увеличение объема на уровне, то всегда добавляем в конец очереди, даже для диффа стаканов
					//var clone = message.TypedClone();
					var clone = _messagePool.Allocate();

					clone.TransactionId = message.TransactionId;
					clone.OrderPrice = message.OrderPrice;
					clone.PortfolioName = message.PortfolioName;
					clone.Balance = byVolume ? message.OrderVolume : message.Balance;
					clone.OrderVolume = message.OrderVolume;

					AddTotalVolume(message.Side, volume);

					var q = pair.Second;
					q.Volume += volume;
					pair.Second = q;
					level.Add(clone);
				}
				else
				{
					if (message.TransactionId == 0)
					{
						var leftBalance = volume;

						// пришел дифф по стакану - начиная с конца убираем снятый объем
						for (var i = level.Count - 1; i >= 0 && leftBalance > 0; i--)
						{
							var quote = level[i];

							if (quote.TransactionId != message.TransactionId)
								continue;

							var balance = quote.GetBalance();
							leftBalance -= balance;

							if (leftBalance < 0)
							{
								leftBalance = -leftBalance;

								//var clone = message.TypedClone();
								var clone = _messagePool.Allocate();

								clone.TransactionId = message.TransactionId;
								clone.OrderPrice = message.OrderPrice;
								clone.PortfolioName = message.PortfolioName;
								clone.Balance = leftBalance;
								clone.OrderVolume = message.OrderVolume;

								var diff = leftBalance - balance;
								AddTotalVolume(message.Side, diff);

								var q1 = pair.Second;
								q1.Volume += diff;
								pair.Second = q1;

								level[i] = clone;
								break;
							}

							AddTotalVolume(message.Side, -balance);

							var q = pair.Second;
							q.Volume -= balance;
							pair.Second = q;
							level.RemoveAt(i, quote);
							_messagePool.Free(quote);
						}
					}
					else
					{
						if (level.TryGetByTransactionId(message.TransactionId, out var quote))
						{
							var balance = quote.GetBalance();

							AddTotalVolume(message.Side, -balance);

							var q = pair.Second;
							q.Volume -= balance;
							pair.Second = q;
							level.Remove(quote);
							_messagePool.Free(quote);
						}
					}

					if (level.Count == 0)
						quotes.Remove(message.OrderPrice);
				}
			}

			private void AddTotalVolume(Sides side, decimal diff)
			{
				if (side == Sides.Buy)
					_totalBidVolume += diff;
				else
					_totalAskVolume += diff;
			}

			private decimal GetTotalVolume(Sides side)
			{
				return side == Sides.Buy ? _totalBidVolume : _totalAskVolume;
			}

#if EMU_DBG
			private void Verify(Sides side)
			{
				var totalVolume = side == Sides.Buy ? _totalBidVolume : _totalAskVolume;

				if (totalVolume < 0)
					throw new InvalidOperationException();

				if (GetQuotes(side).Values.Sum(p => p.Second.Volume) != totalVolume)
					throw new InvalidOperationException();
			}
#endif

			private void ProcessPendingExecutions(Message message, ICollection<Message> result)
			{
				if (_pendingExecutions.Count == 0)
					return;

				var diff = message.LocalTime - _prevTime;

				foreach (var pair in _pendingExecutions.ToArray())
				{
					var orderMsg = pair.Key;
					var left = pair.Value;
					left -= diff;

					if (left <= TimeSpan.Zero)
					{
						_pendingExecutions.Remove(orderMsg);
						AcceptExecution(message.LocalTime, orderMsg, result);
					}
					else
						_pendingExecutions[orderMsg] = left;
				}
			}

			private ExecutionMessage ToOrder(DateTimeOffset time, ExecutionMessage message)
			{
				return new ExecutionMessage
				{
					LocalTime = time,
					SecurityId = message.SecurityId,
					OrderId = message.OrderId,
					OriginalTransactionId = message.TransactionId,
					Balance = message.Balance,
					OrderState = message.OrderState,
					PortfolioName = message.PortfolioName,
					DataTypeEx = DataType.Transactions,
					HasOrderInfo = true,
					ServerTime = GetServerTime(time),
					StrategyId = message.StrategyId,
				};
			}

			private void ProcessOwnTrade(DateTimeOffset time, ExecutionMessage order, decimal price, decimal volume, ICollection<Message> result)
			{
				if (volume <= 0)
					throw new ArgumentOutOfRangeException(nameof(volume), volume, LocalizedStrings.Str1219);

				var tradeMsg = new ExecutionMessage
				{
					LocalTime = time,
					SecurityId = order.SecurityId,
					OrderId = order.OrderId,
					OriginalTransactionId = order.TransactionId,
					TradeId = _parent.TradeIdGenerator.GetNextId(),
					TradePrice = price,
					TradeVolume = volume,
					DataTypeEx = DataType.Transactions,
					ServerTime = GetServerTime(time),
					Side = order.Side,
					StrategyId = order.StrategyId,
				};
				result.Add(tradeMsg);

				this.AddInfoLog(LocalizedStrings.Str1168Params, tradeMsg.TradeId, tradeMsg.OriginalTransactionId, price, volume);
				var info = _parent.GetPortfolioInfo(order.PortfolioName);

				info.ProcessMyTrade(order.Side, tradeMsg, result);

				result.Add(new ExecutionMessage
				{
					LocalTime = time,
					SecurityId = tradeMsg.SecurityId,
					TradeId = tradeMsg.TradeId,
					TradePrice = tradeMsg.TradePrice,
					TradeVolume = tradeMsg.TradeVolume,
					DataTypeEx = DataType.Ticks,
					ServerTime = GetServerTime(time),
				});
			}

			private DateTimeOffset GetServerTime(DateTimeOffset time)
			{
				if (!_settings.ConvertTime)
					return time;

				var destTimeZone = _settings.TimeZone;

				if (destTimeZone == null)
				{
					var board = _parent._boardDefinitions.TryGetValue(_securityId.BoardCode);

					if (board != null)
						destTimeZone = board.TimeZone;
				}

				if (destTimeZone == null)
					return time;

				//var sourceZone = time.Kind == DateTimeKind.Utc ? TimeZoneInfo.Utc : TimeZoneInfo.Local;

				return TimeZoneInfo.ConvertTime(time, destTimeZone);//.ApplyTimeZone(destTimeZone);
			}

			public decimal GetMarginPrice(Sides side)
			{
				var field = side == Sides.Buy ? Level1Fields.MarginBuy : Level1Fields.MarginSell;
				return (decimal?)_parent._secStates.TryGetValue(_securityId)?.TryGetValue(field) ?? GetQuotes(side).FirstOr()?.Key ?? 0;
			}
		}

		private sealed class PortfolioEmulator
		{
			private class MoneyInfo
			{
				private readonly SecurityMarketEmulator _secEmu;

				public MoneyInfo(SecurityMarketEmulator secEmu)
				{
					_secEmu = secEmu;
				}

				public decimal PositionBeginValue;
				public decimal PositionDiff;
				public decimal PositionCurrentValue => PositionBeginValue + PositionDiff;

				public decimal PositionAveragePrice;

				public decimal PositionPrice
				{
					get
					{
						var pos = PositionCurrentValue;

						if (pos == 0)
							return 0;

						return pos.Abs() * PositionAveragePrice;
					}
				}

				public decimal TotalPrice => GetPrice(0, 0);

				public decimal GetPrice(decimal buyVol, decimal sellVol)
				{
					var totalMoney = PositionPrice;

					var buyOrderPrice = (TotalBidsVolume + buyVol) * _secEmu.GetMarginPrice(Sides.Buy);
					var sellOrderPrice = (TotalAsksVolume + sellVol) * _secEmu.GetMarginPrice(Sides.Sell);

					if (totalMoney != 0)
					{
						if (PositionCurrentValue > 0)
						{
							totalMoney += buyOrderPrice;
							totalMoney = totalMoney.Max(sellOrderPrice);
						}
						else
						{
							totalMoney += sellOrderPrice;
							totalMoney = totalMoney.Max(buyOrderPrice);
						}
					}
					else
					{
						totalMoney = buyOrderPrice + sellOrderPrice;
					}

					return totalMoney;
				}

				public decimal TotalBidsVolume;
				public decimal TotalAsksVolume;
			}

			private readonly MarketEmulator _parent;
			private readonly string _name;
			private readonly Dictionary<SecurityId, MoneyInfo> _moneys = new();

			private decimal _beginMoney;
			private decimal _currentMoney;

			private decimal _totalBlockedMoney;

			public PortfolioPnLManager PnLManager { get; }

			public PortfolioEmulator(MarketEmulator parent, string name)
			{
				_parent = parent;
				_name = name;

				PnLManager = new PortfolioPnLManager(name);
			}

			public void RequestState(PortfolioLookupMessage pfMsg, ICollection<Message> result)
			{
				var time = pfMsg.LocalTime;

				AddPortfolioChangeMessage(time, result);

				foreach (var pair in _moneys)
				{
					var money = pair.Value;

					result.Add(
						new PositionChangeMessage
						{
							LocalTime = time,
							ServerTime = time,
							PortfolioName = _name,
							SecurityId = pair.Key,
							OriginalTransactionId = pfMsg.TransactionId,
							StrategyId = pfMsg.StrategyId,
						}
						.Add(PositionChangeTypes.CurrentValue, money.PositionCurrentValue)
						.TryAdd(PositionChangeTypes.AveragePrice, money.PositionAveragePrice)
					);
				}
			}

			public void ProcessPositionChange(PositionChangeMessage posMsg, ICollection<Message> result)
			{
				var beginValue = (decimal?)posMsg.Changes.TryGetValue(PositionChangeTypes.BeginValue);

				if (posMsg.IsMoney())
				{
					if (beginValue == null)
						return;

					_currentMoney = _beginMoney = (decimal)beginValue;

					AddPortfolioChangeMessage(posMsg.ServerTime, result);
					return;
				}

				//if (!_moneys.ContainsKey(posMsg.SecurityId))
				//{
				//	result.Add(new PositionMessage
				//	{
				//		SecurityId = posMsg.SecurityId,
				//		PortfolioName = posMsg.PortfolioName,
				//		DepoName = posMsg.DepoName,
				//		LocalTime = posMsg.LocalTime
				//	});
				//}

				var money = GetMoney(posMsg.SecurityId/*, posMsg.LocalTime, result*/);

				var prevPrice = money.PositionPrice;

				money.PositionBeginValue = beginValue ?? 0L;
				money.PositionAveragePrice = posMsg.Changes.TryGetValue(PositionChangeTypes.AveragePrice).To<decimal?>() ?? money.PositionAveragePrice;

				//if (beginValue == 0m)
				//	return;

				result.Add(posMsg.Clone());

				_totalBlockedMoney = _totalBlockedMoney - prevPrice + money.PositionPrice;

				result.Add(
					new PositionChangeMessage
					{
						SecurityId = SecurityId.Money,
						ServerTime = posMsg.ServerTime,
						LocalTime = posMsg.LocalTime,
						PortfolioName = _name,
						StrategyId = posMsg.StrategyId,
					}.Add(PositionChangeTypes.BlockedValue, _totalBlockedMoney)
				);
			}

			private MoneyInfo GetMoney(SecurityId securityId/*, DateTimeOffset time, ICollection<Message> result*/)
			{
				//bool isNew;
				var money = _moneys.SafeAdd(securityId, k => new MoneyInfo(_parent.GetEmulator(securityId)));

				//if (isNew)
				//{
				//	result.Add(new PositionMessage
				//	{
				//		LocalTime = time,
				//		PortfolioName = _name,
				//		SecurityId = securityId,
				//	});
				//}

				return money;
			}

			public decimal? ProcessOrder(ExecutionMessage orderMsg, decimal? cancelBalance, ICollection<Message> result)
			{
				var money = GetMoney(orderMsg.SecurityId/*, orderMsg.LocalTime, result*/);

				var prevPrice = money.TotalPrice;

				if (cancelBalance == null)
				{
					var balance = orderMsg.SafeGetVolume();

					if (orderMsg.Side == Sides.Buy)
						money.TotalBidsVolume += balance;
					else
						money.TotalAsksVolume += balance;
				}
				else
				{
					if (orderMsg.Side == Sides.Buy)
						money.TotalBidsVolume -= cancelBalance.Value;
					else
						money.TotalAsksVolume -= cancelBalance.Value;
				}

				_totalBlockedMoney = _totalBlockedMoney - prevPrice + money.TotalPrice;

				var commission = _parent._commissionManager.Process(orderMsg);

				AddPortfolioChangeMessage(orderMsg.ServerTime, result);

				return commission;
			}

			public void ProcessMyTrade(Sides side, ExecutionMessage tradeMsg, ICollection<Message> result)
			{
				var time = tradeMsg.ServerTime;

				PnLManager.ProcessMyTrade(tradeMsg, out _);
				tradeMsg.Commission = _parent._commissionManager.Process(tradeMsg);

				var position = tradeMsg.TradeVolume;

				if (position == null)
					return;

				if (side == Sides.Sell)
					position *= -1;

				var money = GetMoney(tradeMsg.SecurityId/*, time, result*/);

				var prevPrice = money.TotalPrice;

				var tradeVol = tradeMsg.TradeVolume.Value;

				if (tradeMsg.Side == Sides.Buy)
					money.TotalBidsVolume -= tradeVol;
				else
					money.TotalAsksVolume -= tradeVol;

				var prevPos = money.PositionCurrentValue;

				money.PositionDiff += position.Value;

				var tradePrice = tradeMsg.TradePrice.Value;
				var currPos = money.PositionCurrentValue;

				if (prevPos.Sign() == currPos.Sign())
					money.PositionAveragePrice = (money.PositionAveragePrice * prevPos + position.Value * tradePrice) / currPos;
				else
					money.PositionAveragePrice = currPos == 0 ? 0 : tradePrice;

				_totalBlockedMoney = _totalBlockedMoney - prevPrice + money.TotalPrice;

				result.Add(
					new PositionChangeMessage
					{
						LocalTime = time,
						ServerTime = time,
						PortfolioName = _name,
						SecurityId = tradeMsg.SecurityId,
						StrategyId = tradeMsg.StrategyId,
					}
					.Add(PositionChangeTypes.CurrentValue, money.PositionCurrentValue)
					.TryAdd(PositionChangeTypes.AveragePrice, money.PositionAveragePrice)
				);

				AddPortfolioChangeMessage(time, result);
			}

			public void ProcessMarginChange(DateTimeOffset time, SecurityId securityId, ICollection<Message> result)
			{
				var money = _moneys.TryGetValue(securityId);

				if (money == null)
					return;

				_totalBlockedMoney = 0;

				foreach (var pair in _moneys)
					_totalBlockedMoney += pair.Value.TotalPrice;

				result.Add(
					new PositionChangeMessage
					{
						SecurityId = SecurityId.Money,
						ServerTime = time,
						LocalTime = time,
						PortfolioName = _name,
					}.Add(PositionChangeTypes.BlockedValue, _totalBlockedMoney)
				);
			}

			public void AddPortfolioChangeMessage(DateTimeOffset time, ICollection<Message> result)
			{
				var realizedPnL = PnLManager.RealizedPnL;
				var unrealizedPnL = PnLManager.UnrealizedPnL;
				var commission = _parent._commissionManager.Commission;
				var totalPnL = PnLManager.PnL - commission;

				try
				{
					_currentMoney = _beginMoney + totalPnL;
				}
				catch (OverflowException ex)
				{
					result.Add(ex.ToErrorMessage());
				}

				result.Add(new PositionChangeMessage
				{
					SecurityId = SecurityId.Money,
					ServerTime = time,
					LocalTime = time,
					PortfolioName = _name,
				}
				.Add(PositionChangeTypes.RealizedPnL, realizedPnL)
				.TryAdd(PositionChangeTypes.UnrealizedPnL, unrealizedPnL, true)
				.Add(PositionChangeTypes.VariationMargin, totalPnL)
				.Add(PositionChangeTypes.CurrentValue, _currentMoney)
				.Add(PositionChangeTypes.BlockedValue, _totalBlockedMoney)
				.Add(PositionChangeTypes.Commission, commission));
			}

			public InvalidOperationException CheckRegistration(ExecutionMessage execMsg)
			{
				var settings = _parent.Settings;

				if (settings.CheckMoney)
				{
					// если задан баланс, то проверям по нему (для частично исполненных заявок)
					var volume = execMsg.Balance ?? execMsg.SafeGetVolume();

					var money = GetMoney(execMsg.SecurityId/*, execMsg.LocalTime, result*/);

					var needBlock = money.GetPrice(execMsg.Side == Sides.Buy ? volume : 0, execMsg.Side == Sides.Sell ? volume : 0);

					if (_currentMoney < needBlock)
					{
						return new InsufficientFundException(LocalizedStrings.Str1169Params
							.Put(execMsg.PortfolioName, execMsg.TransactionId, needBlock, _currentMoney, money.TotalPrice));
					}
				}
				else if (settings.CheckShortable && execMsg.Side == Sides.Sell &&
						 _parent._securityEmulators.TryGetValue(execMsg.SecurityId, out var secEmu) &&
						 secEmu.SecurityDefinition?.Shortable == false)
				{
					var money = GetMoney(execMsg.SecurityId/*, execMsg.LocalTime, result*/);

					var potentialPosition = money.PositionCurrentValue - execMsg.OrderVolume;

					if (potentialPosition < 0)
					{
						return new(LocalizedStrings.CannotShortPosition
							.Put(execMsg.PortfolioName, execMsg.TransactionId, money.PositionCurrentValue, execMsg.OrderVolume));
					}
				}

				return null;
			}
		}

		private readonly Dictionary<SecurityId, SecurityMarketEmulator> _securityEmulators = new();
		private readonly Dictionary<string, List<SecurityMarketEmulator>> _securityEmulatorsByBoard = new(StringComparer.InvariantCultureIgnoreCase);
		private readonly Dictionary<string, PortfolioEmulator> _portfolios = new();
		private readonly Dictionary<string, BoardMessage> _boardDefinitions = new(StringComparer.InvariantCultureIgnoreCase);
		private readonly Dictionary<SecurityId, Dictionary<Level1Fields, object>> _secStates = new();
		private bool? _needBuffer;
		private readonly List<Message> _buffer = new();
		private DateTimeOffset _bufferPrevFlush;
		private DateTimeOffset _portfoliosPrevRecalc;
		private readonly ICommissionManager _commissionManager = new CommissionManager();
		private readonly Dictionary<string, SessionStates> _boardStates = new(StringComparer.InvariantCultureIgnoreCase);

		/// <summary>
		/// Initializes a new instance of the <see cref="MarketEmulator"/>.
		/// </summary>
		/// <param name="securityProvider">The provider of information about instruments.</param>
		/// <param name="portfolioProvider">The portfolio to be used to register orders. If value is not given, the portfolio with default name Simulator will be created.</param>
		/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
		/// <param name="transactionIdGenerator">Transaction id generator.</param>
		public MarketEmulator(ISecurityProvider securityProvider, IPortfolioProvider portfolioProvider, IExchangeInfoProvider exchangeInfoProvider, IdGenerator transactionIdGenerator)
		{
			SecurityProvider = securityProvider ?? throw new ArgumentNullException(nameof(securityProvider));
			PortfolioProvider = portfolioProvider ?? throw new ArgumentNullException(nameof(portfolioProvider));
			ExchangeInfoProvider = exchangeInfoProvider ?? throw new ArgumentNullException(nameof(exchangeInfoProvider));
			TransactionIdGenerator = transactionIdGenerator ?? throw new ArgumentNullException(nameof(transactionIdGenerator));

			((IMessageAdapter)this).SupportedInMessages = ((IMessageAdapter)this).PossibleSupportedMessages.Select(i => i.Type).ToArray();
		}

		/// <inheritdoc />
		public ISecurityProvider SecurityProvider { get; }

		/// <inheritdoc />
		public IPortfolioProvider PortfolioProvider { get; }

		/// <inheritdoc />
		public IExchangeInfoProvider ExchangeInfoProvider { get; }

		/// <summary>
		/// Transaction id generator.
		/// </summary>
		public IdGenerator TransactionIdGenerator { get; }

		/// <inheritdoc />
		public MarketEmulatorSettings Settings { get; } = new MarketEmulatorSettings();

		/// <summary>
		/// The number of processed messages.
		/// </summary>
		public long ProcessedMessageCount { get; private set; }

		/// <summary>
		/// The generator of identifiers for orders.
		/// </summary>
		public IncrementalIdGenerator OrderIdGenerator { get; set; } = new IncrementalIdGenerator();

		/// <summary>
		/// The generator of identifiers for trades.
		/// </summary>
		public IncrementalIdGenerator TradeIdGenerator { get; set; } = new IncrementalIdGenerator();

		private DateTimeOffset _currentTime;

		/// <inheritdoc />
		public override DateTimeOffset CurrentTime => _currentTime;

		/// <inheritdoc />
		public bool SendInMessage(Message message)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var retVal = new List<Message>();

			switch (message.Type)
			{
				case MessageTypes.Time:
				{
					foreach (var securityEmulator in _securityEmulators.Values)
						securityEmulator.Process(message, retVal);

					// время у TimeMsg может быть больше времени сообщений из эмулятора
					//retVal.Add(message);

					break;
				}

				case MessageTypes.Execution:
				{
					var execMsg = (ExecutionMessage)message;
					GetEmulator(execMsg.SecurityId).Process(message, retVal);
					break;
				}

				case MessageTypes.QuoteChange:
				{
					var quoteMsg = (QuoteChangeMessage)message;
					GetEmulator(quoteMsg.SecurityId).Process(message, retVal);
					break;
				}

				case MessageTypes.OrderRegister:
				case MessageTypes.OrderReplace:
				case MessageTypes.OrderCancel:
				{
					var orderMsg = (OrderMessage)message;
					var secId = orderMsg.SecurityId;

					var canRegister = true;

					if (Settings.CheckTradingState)
					{
						var state = _boardStates.TryGetValue2(nameof(MarketEmulator)) ?? _boardStates.TryGetValue2(secId.BoardCode);

						switch (state)
						{
							case SessionStates.Paused:
							case SessionStates.ForceStopped:
							case SessionStates.Ended:
							{
								retVal.Add(new ExecutionMessage
								{
									DataTypeEx = DataType.Transactions,
									HasOrderInfo = true,
									ServerTime = orderMsg.LocalTime,
									LocalTime = orderMsg.LocalTime,
									OriginalTransactionId = orderMsg.TransactionId,
									OrderState = OrderStates.Failed,
									StrategyId = orderMsg.StrategyId,
									Error = new InvalidOperationException(LocalizedStrings.SessionStopped.Put(secId.BoardCode, state.Value)),
								});

								canRegister = false;
								break;
							}
						}
					}

					if (canRegister)
						GetEmulator(secId).Process(message, retVal);

					break;
				}

				case MessageTypes.Reset:
				{
					_securityEmulators.Clear();
					_securityEmulatorsByBoard.Clear();

					OrderIdGenerator.Current = Settings.InitialOrderId;
					TradeIdGenerator.Current = Settings.InitialTradeId;

					_portfolios.Clear();
					_boardDefinitions.Clear();
					_boardStates.Clear();

					_secStates.Clear();

					_buffer.Clear();
					_needBuffer = null;

					_bufferPrevFlush = default;
					_portfoliosPrevRecalc = default;

					ProcessedMessageCount = 0;

					retVal.Add(new ResetMessage());
					break;
				}

				case MessageTypes.Connect:
				{
					_portfolios.SafeAdd(Extensions.SimulatorPortfolioName, key => new PortfolioEmulator(this, key));
					retVal.Add(new ConnectMessage());
					break;
				}

				//case ExtendedMessageTypes.Clearing:
				//{
				//	var clearingMsg = (ClearingMessage)message;
				//	var emu = _securityEmulators.TryGetValue(clearingMsg.SecurityId);

				//	if (emu != null)
				//	{
				//		_securityEmulators.Remove(clearingMsg.SecurityId);

				//		var emulators = _securityEmulatorsByBoard.TryGetValue(clearingMsg.SecurityId.BoardCode);

				//		if (emulators != null)
				//		{
				//			if (emulators.Remove(emu) && emulators.Count == 0)
				//				_securityEmulatorsByBoard.Remove(clearingMsg.SecurityId.BoardCode);
				//		}
				//	}

				//	break;
				//}

				//case MessageTypes.PortfolioChange:
				//{
				//	var pfChangeMsg = (PortfolioChangeMessage)message;
				//	GetPortfolioInfo(pfChangeMsg.PortfolioName).ProcessPortfolioChange(pfChangeMsg, retVal);
				//	break;
				//}

				case MessageTypes.PositionChange:
				{
					var posChangeMsg = (PositionChangeMessage)message;
					GetPortfolioInfo(posChangeMsg.PortfolioName).ProcessPositionChange(posChangeMsg, retVal);
					break;
				}

				case MessageTypes.Board:
				{
					var boardMsg = (BoardMessage)message;

					_boardDefinitions[boardMsg.Code] = boardMsg.TypedClone();

					var emulators = _securityEmulatorsByBoard.TryGetValue(boardMsg.Code);

					if (emulators != null)
					{
						foreach (var securityEmulator in emulators)
							securityEmulator.Process(boardMsg, retVal);
					}

					break;
				}

				case MessageTypes.Level1Change:
				{
					var level1Msg = (Level1ChangeMessage)message;
					GetEmulator(level1Msg.SecurityId).Process(level1Msg, retVal);
					UpdateLevel1Info(level1Msg, retVal, false);
					break;
				}

				case MessageTypes.Portfolio:
				{
					var pfMsg = (PortfolioMessage)message;

					retVal.Add(pfMsg);
					//GetPortfolioInfo(pfMsg.PortfolioName);

					break;
				}

				case MessageTypes.OrderStatus:
				{
					var statusMsg = (OrderStatusMessage)message;

					if (!statusMsg.IsSubscribe)
						break;

					foreach (var pair in _securityEmulators)
					{
						pair.Value.Process(message, retVal);
					}

					if (statusMsg.To == null)
						retVal.Add(new SubscriptionOnlineMessage { OriginalTransactionId = statusMsg.TransactionId });

					break;
				}

				case MessageTypes.PortfolioLookup:
				{
					var lookupMsg = (PortfolioLookupMessage)message;

					if (!lookupMsg.IsSubscribe)
						break;

					if (lookupMsg.PortfolioName.IsEmpty())
					{
						foreach (var pair in _portfolios)
						{
							retVal.Add(new PortfolioMessage
							{
								PortfolioName = pair.Key,
								OriginalTransactionId = lookupMsg.TransactionId
							});

							pair.Value.RequestState(lookupMsg, retVal);
						}
					}
					else
					{
						retVal.Add(new PortfolioMessage
						{
							PortfolioName = lookupMsg.PortfolioName,
							OriginalTransactionId = lookupMsg.TransactionId
						});

						if (_portfolios.TryGetValue(lookupMsg.PortfolioName, out var pfEmu))
						{
							pfEmu.RequestState(lookupMsg, retVal);
						}
					}

					retVal.Add(lookupMsg.CreateResult());

					break;
				}

				case MessageTypes.Security:
				{
					var secMsg = (SecurityMessage)message;

					//retVal.Add(secMsg);
					GetEmulator(secMsg.SecurityId).Process(secMsg, retVal);

					break;
				}

				case ExtendedMessageTypes.CommissionRule:
				{
					var ruleMsg = (CommissionRuleMessage)message;
					_commissionManager.Rules.Add(ruleMsg.Rule);
					break;
				}

				case MessageTypes.BoardState:
				{
					if (Settings.CheckTradingState)
					{
						var boardStateMsg = (BoardStateMessage)message;

						var board = boardStateMsg.BoardCode;

						if (board.IsEmpty())
							board = nameof(MarketEmulator);

						_boardStates[board] = boardStateMsg.State;
					}

					retVal.Add(message);

					break;
				}

				case MessageTypes.MarketData:
				{
					var secId = ((MarketDataMessage)message).SecurityId;

					if (!secId.IsAllSecurity())
						GetEmulator(secId).Process(message, retVal);

					break;
				}

				case MessageTypes.SecurityLookup:
				case MessageTypes.BoardLookup:
				case MessageTypes.TimeFrameLookup:
				{
					// result will be sends as a loopback from underlying market data adapter
					break;
				}

				case MessageTypes.SubscriptionResponse:
				case MessageTypes.SubscriptionFinished:
				case MessageTypes.SubscriptionOnline:
				{
					retVal.Add(message.TypedClone());
					break;
				}

				default:
				{
					if (message is CandleMessage candleMsg)
						GetEmulator(candleMsg.SecurityId).Process(candleMsg, retVal);
					else
						retVal.Add(message);

					break;
				}
			}

			if (message.Type != MessageTypes.Reset)
				ProcessedMessageCount++;

			RecalcPnL(message.LocalTime, retVal);

			var allowStore = Settings.AllowStoreGenerateMessages;

			foreach (var msg in BufferResult(retVal, message.LocalTime))
			{
				if (!allowStore)
					msg.OfflineMode = MessageOfflineModes.Ignore;

				RaiseNewOutMessage(msg);
			}

			return true;
		}

		/// <inheritdoc />
		public event Action<Message> NewOutMessage;

		private void RaiseNewOutMessage(Message message)
		{
			_currentTime = message.LocalTime;
			NewOutMessage?.Invoke(message);
		}

		private SecurityMarketEmulator GetEmulator(SecurityId securityId)
		{
			// force hash code caching
			securityId.GetHashCode();

			return _securityEmulators.SafeAdd(securityId, key =>
			{
				var emulator = new SecurityMarketEmulator(this, securityId) { Parent = this };

				_securityEmulatorsByBoard.SafeAdd(securityId.BoardCode).Add(emulator);

				var sec = SecurityProvider.LookupById(securityId);

				if (sec != null)
					emulator.Process(sec.ToMessage(), new List<Message>());

				var board = _boardDefinitions.TryGetValue(securityId.BoardCode);

				if (board != null)
					emulator.Process(board, new List<Message>());

				return emulator;
			});
		}

		private IEnumerable<Message> BufferResult(IEnumerable<Message> result, DateTimeOffset time)
		{
			_needBuffer ??= Settings.BufferTime > TimeSpan.Zero;

			if (_needBuffer == false)
				return result;

			_buffer.AddRange(result);

			if ((time - _bufferPrevFlush) > Settings.BufferTime)
			{
				_bufferPrevFlush = time;
				return _buffer.CopyAndClear();
			}
			else
			{
				return Enumerable.Empty<Message>();
			}
		}

		private PortfolioEmulator GetPortfolioInfo(string portfolioName)
		{
			return _portfolios.SafeAdd(portfolioName, key => new PortfolioEmulator(this, key));
		}

		private void UpdateLevel1Info(Level1ChangeMessage level1Msg, ICollection<Message> retVal, bool addToResult)
		{
			var marginChanged = false;
			var state = _secStates.SafeAdd(level1Msg.SecurityId);

			foreach (var change in level1Msg.Changes)
			{
				switch (change.Key)
				{
					case Level1Fields.PriceStep:
					case Level1Fields.VolumeStep:
					case Level1Fields.MinPrice:
					case Level1Fields.MaxPrice:
						state[change.Key] = change.Value;
						break;

					case Level1Fields.State:
						if (Settings.CheckTradingState)
							state[change.Key] = change.Value;

						break;

					case Level1Fields.MarginBuy:
					case Level1Fields.MarginSell:
					{
						var oldValue = state.TryGetValue(change.Key);

						if (oldValue != null && (decimal)oldValue == (decimal)change.Value)
							break;

						state[change.Key] = change.Value;
						marginChanged = true;

						break;
					}
				}
			}

			if (addToResult)
				retVal.Add(level1Msg);

			if (!marginChanged)
				return;

			foreach (var info in _portfolios.Values)
				info.ProcessMarginChange(level1Msg.LocalTime, level1Msg.SecurityId, retVal);
		}

		private InvalidOperationException CheckRegistration(ExecutionMessage execMsg, SecurityMessage securityDefinition, bool priceStepExplicit, bool volumeStepExplicit/*, ICollection<Message> result*/)
		{
			if (Settings.CheckTradingState)
			{
				var board = _boardDefinitions.TryGetValue(execMsg.SecurityId.BoardCode);

				if (board != null)
				{
					//if (execMsg.OrderType == OrderTypes.Market && !board.IsSupportMarketOrders)
					//if (!Settings.IsSupportAtomicReRegister)
					//	return new(LocalizedStrings.Str1170Params.Put(board.Code));

					if (!board.IsTradeTime(execMsg.ServerTime))
						return new(LocalizedStrings.Str1171);
				}
			}

			var state = _secStates.TryGetValue(execMsg.SecurityId);

			var secState = (SecurityStates?)state?.TryGetValue(Level1Fields.State);

			if (secState == SecurityStates.Stoped)
				return new(LocalizedStrings.SecurityStopped.Put(execMsg.SecurityId));

			if (securityDefinition?.BasketCode.IsEmpty() == false)
				return new(LocalizedStrings.SecurityNonTradable.Put(execMsg.SecurityId));

			var priceStep = securityDefinition?.PriceStep;
			var volumeStep = securityDefinition?.VolumeStep;
			var minVolume = securityDefinition?.MinVolume;
			var maxVolume = securityDefinition?.MaxVolume;

			if (state != null && execMsg.OrderType != OrderTypes.Market)
			{
				var minPrice = (decimal?)state.TryGetValue(Level1Fields.MinPrice);
				var maxPrice = (decimal?)state.TryGetValue(Level1Fields.MaxPrice);

				priceStep ??= (decimal?)state.TryGetValue(Level1Fields.PriceStep);

				if (minPrice != null && minPrice > 0 && execMsg.OrderPrice < minPrice)
					return new(LocalizedStrings.Str1172Params.Put(execMsg.OrderPrice, execMsg.TransactionId, minPrice));

				if (maxPrice != null && maxPrice > 0 && execMsg.OrderPrice > maxPrice)
					return new(LocalizedStrings.Str1173Params.Put(execMsg.OrderPrice, execMsg.TransactionId, maxPrice));
			}

			if (priceStepExplicit && priceStep != null && priceStep > 0 && execMsg.OrderPrice % priceStep != 0)
				return new(LocalizedStrings.OrderPriceNotMultipleOfPriceStep.Put(execMsg.OrderPrice, execMsg.TransactionId, priceStep));

			volumeStep ??= (decimal?)state?.TryGetValue(Level1Fields.VolumeStep);

			if (volumeStepExplicit && volumeStep != null && volumeStep > 0 && execMsg.OrderVolume % volumeStep != 0)
				return new(LocalizedStrings.OrderVolumeNotMultipleOfVolumeStep.Put(execMsg.OrderVolume, execMsg.TransactionId, volumeStep));

			if (minVolume != null && execMsg.OrderVolume < minVolume)
				return new(LocalizedStrings.OrderVolumeLessMin.Put(execMsg.OrderVolume, execMsg.TransactionId, minVolume));

			if (maxVolume != null && execMsg.OrderVolume > maxVolume)
				return new(LocalizedStrings.OrderVolumeMoreMax.Put(execMsg.OrderVolume, execMsg.TransactionId, maxVolume));

			return GetPortfolioInfo(execMsg.PortfolioName).CheckRegistration(execMsg/*, result*/);
		}

		private void RecalcPnL(DateTimeOffset time, ICollection<Message> messages)
		{
			if (Settings.PortfolioRecalcInterval == TimeSpan.Zero)
				return;

			if (time - _portfoliosPrevRecalc <= Settings.PortfolioRecalcInterval)
				return;

			foreach (var message in messages)
			{
				foreach (var emulator in _portfolios.Values)
					emulator.PnLManager.ProcessMessage(message);

				time = message.LocalTime;
			}

			foreach (var emulator in _portfolios.Values)
				emulator.AddPortfolioChangeMessage(time, messages);

			_portfoliosPrevRecalc = time;
		}

		ChannelStates IMessageChannel.State => ChannelStates.Started;

		IdGenerator IMessageAdapter.TransactionIdGenerator { get; } = new IncrementalIdGenerator();

		IEnumerable<MessageTypeInfo> IMessageAdapter.PossibleSupportedMessages { get; } = new[]
		{
			MessageTypes.SecurityLookup.ToInfo(),
			MessageTypes.TimeFrameLookup.ToInfo(),
			MessageTypes.BoardLookup.ToInfo(),
			MessageTypes.MarketData.ToInfo(),
			MessageTypes.PortfolioLookup.ToInfo(),
			MessageTypes.OrderStatus.ToInfo(),
			MessageTypes.OrderRegister.ToInfo(),
			MessageTypes.OrderCancel.ToInfo(),
			MessageTypes.OrderReplace.ToInfo(),
			MessageTypes.OrderGroupCancel.ToInfo(),
			MessageTypes.BoardState.ToInfo(),
			MessageTypes.Security.ToInfo(),
			MessageTypes.Portfolio.ToInfo(),
			MessageTypes.Board.ToInfo(),
			MessageTypes.Reset.ToInfo(),
			MessageTypes.QuoteChange.ToInfo(),
			MessageTypes.Level1Change.ToInfo(),
			MessageTypes.EmulationState.ToInfo(),
			ExtendedMessageTypes.CommissionRule.ToInfo(),
			//ExtendedMessageTypes.Clearing.ToInfo(),
		};
		IEnumerable<MessageTypes> IMessageAdapter.SupportedInMessages { get; set; }
		IEnumerable<MessageTypes> IMessageAdapter.SupportedOutMessages { get; } = Enumerable.Empty<MessageTypes>();
		IEnumerable<MessageTypes> IMessageAdapter.SupportedResultMessages { get; } = new[]
		{
			MessageTypes.SecurityLookup,
			MessageTypes.PortfolioLookup,
			MessageTypes.TimeFrameLookup,
			MessageTypes.BoardLookup,
		};
		IEnumerable<DataType> IMessageAdapter.SupportedMarketDataTypes { get; } = new[]
		{
			DataType.OrderLog,
			DataType.Ticks,
			DataType.CandleTimeFrame,
			DataType.MarketDepth,
		};

		IDictionary<string, RefPair<SecurityTypes, string>> IMessageAdapter.SecurityClassInfo { get; } = new Dictionary<string, RefPair<SecurityTypes, string>>();

		IEnumerable<Level1Fields> IMessageAdapter.CandlesBuildFrom => Enumerable.Empty<Level1Fields>();

		bool IMessageAdapter.CheckTimeFrameByRequest => true;

		ReConnectionSettings IMessageAdapter.ReConnectionSettings { get; } = new ReConnectionSettings();

		TimeSpan IMessageAdapter.HeartbeatInterval { get => TimeSpan.Zero; set { } }

		string IMessageAdapter.StorageName => null;

		bool IMessageAdapter.IsNativeIdentifiersPersistable => false;
		bool IMessageAdapter.IsNativeIdentifiers => false;
		bool IMessageAdapter.IsFullCandlesOnly => false;
		bool IMessageAdapter.IsSupportSubscriptions => true;
		bool IMessageAdapter.IsSupportCandlesUpdates => true;
		bool IMessageAdapter.IsSupportCandlesPriceLevels => false;

		MessageAdapterCategories IMessageAdapter.Categories => default;

		IEnumerable<Tuple<string, Type>> IMessageAdapter.SecurityExtendedFields { get; } = Enumerable.Empty<Tuple<string, Type>>();
		IEnumerable<int> IMessageAdapter.SupportedOrderBookDepths => throw new NotImplementedException();
		bool IMessageAdapter.IsSupportOrderBookIncrements => false;
		bool IMessageAdapter.IsSupportExecutionsPnL => true;
		bool IMessageAdapter.IsSecurityNewsOnly => false;
		Type IMessageAdapter.OrderConditionType => null;
		bool IMessageAdapter.HeartbeatBeforConnect => false;
		Uri IMessageAdapter.Icon => null;
		bool IMessageAdapter.IsAutoReplyOnTransactonalUnsubscription => true;
		bool IMessageAdapter.EnqueueSubscriptions { get; set; }
		bool IMessageAdapter.IsSupportTransactionLog => false;
		bool IMessageAdapter.UseChannels => false;
		TimeSpan IMessageAdapter.IterationInterval => default;
		string IMessageAdapter.FeatureName => string.Empty;
		string IMessageAdapter.AssociatedBoard => string.Empty;
		bool? IMessageAdapter.IsPositionsEmulationRequired => null;
		bool IMessageAdapter.IsReplaceCommandEditCurrent => false;
		bool IMessageAdapter.GenerateOrderBookFromLevel1 { get; set; }
		TimeSpan? IMessageAdapter.LookupTimeout => null;

		IOrderLogMarketDepthBuilder IMessageAdapter.CreateOrderLogMarketDepthBuilder(SecurityId securityId)
			=> new OrderLogMarketDepthBuilder(securityId);

		IEnumerable<object> IMessageAdapter.GetCandleArgs(Type candleType, SecurityId securityId, DateTimeOffset? from, DateTimeOffset? to)
			=> Enumerable.Empty<object>();

		TimeSpan IMessageAdapter.GetHistoryStepSize(DataType dataType, out TimeSpan iterationInterval)
		{
			iterationInterval = TimeSpan.Zero;
			return TimeSpan.Zero;
		}

		int? IMessageAdapter.GetMaxCount(DataType dataType) => null;

		bool IMessageAdapter.IsAllDownloadingSupported(DataType dataType) => false;
		bool IMessageAdapter.IsSecurityRequired(DataType dataType) => dataType.IsSecurityRequired;

		void IMessageChannel.Open()
		{
		}

		void IMessageChannel.Close()
		{
		}

		void IMessageChannel.Suspend()
		{
		}

		void IMessageChannel.Resume()
		{
		}

		void IMessageChannel.Clear()
		{
		}

		event Action IMessageChannel.StateChanged
		{
			add { }
			remove { }
		}

		IMessageChannel ICloneable<IMessageChannel>.Clone()
			=> new MarketEmulator(SecurityProvider, PortfolioProvider, ExchangeInfoProvider, TransactionIdGenerator);

		object ICloneable.Clone() => ((ICloneable<IMessageChannel>)this).Clone();
	}
}