﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ecng.Collections;
using Ecng.Common;

using Nito.AsyncEx;

using StockSharp.Localization;
using StockSharp.Logging;

namespace StockSharp.Messages;

/// <summary>
/// Async message processor helper.
/// </summary>
public class AsyncMessageProcessor : BaseLogReceiver
{
	private class MessageQueueItem
	{
		public MessageQueueItem(Message msg, CancellationTokenSource cts)
		{
			Message = msg;

			IsControl = Message.Type
				is MessageTypes.Reset
				or MessageTypes.Connect
				or MessageTypes.Disconnect;

			IsTransaction = Message.Type
				is MessageTypes.OrderRegister
				or MessageTypes.OrderReplace
				or MessageTypes.OrderPairReplace
				or MessageTypes.OrderCancel
				or MessageTypes.OrderGroupCancel;

			Cts = cts;
		}

		public Message Message { get; }
		public CancellationTokenSource Cts { get; }
		public Task Task { get; set; }

		public bool IsStartedProcessing => Task != null;
		public bool IsProcessing => Task?.IsCompleted == false;
		public bool IsDone => Task?.IsCompleted == true;

		public bool IsCanceled => Cts.Token.IsCancellationRequested;

		public bool IsControl { get; }
		public bool IsTransaction { get; }
	}

	private readonly SynchronizedList<MessageQueueItem> _messages = new();

	private readonly SynchronizedDictionary<Task, Func<string>> _childTasks = new();
	private readonly SynchronizedDictionary<long, CancellationTokenSource> _childTokens = new();

	private readonly AsyncManualResetEvent _processMessageEvt = new(false);
	private CancellationTokenSource _globalCts = new();

	private bool _isConnectionStarted, _isDisconnecting;

	private readonly IAsyncMessageAdapter _adapter;

	/// <summary>
	/// Initialize <see cref="AsyncMessageProcessor"/>.
	/// </summary>
	/// <param name="adapter"><see cref="IAsyncMessageAdapter"/>.</param>
	public AsyncMessageProcessor(IAsyncMessageAdapter adapter)
	{
		Parent = _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
		// ReSharper disable once VirtualMemberCallInConstructor
		Name = $"async({adapter.Name})";
		Task.Run(ProcessMessagesAsync);
	}

	/// <inheritdoc />
	protected override void DisposeManaged()
	{
		base.DisposeManaged();
		_processMessageEvt.Set();
	}

	/// <summary>
	/// </summary>
	public bool EnqueueMessage(Message msg)
	{
		this.AddVerboseLog("enqueue: {0}", msg.Type);

		lock (_messages.SyncRoot)
		{
			if (msg is ResetMessage)
				CancelAndReplaceGlobalCts();

			_messages.Add(new(msg, _globalCts));
		}

		_processMessageEvt.Set();

		return true;
	}

	/// <summary></summary>
	public void TryAddChildTask(string name, Task task) => TryAddChildTask(() => name, task);

	/// <summary></summary>
	public void TryAddChildTask(string name, ValueTask task) => TryAddChildTask(() => name, task);

	/// <summary>
	/// </summary>
	public void TryAddChildTask(Func<string> getName, Task task)
	{
		if(!task.IsCompleted)
		{
			_childTasks.Add(task, getName ?? throw new ArgumentNullException(nameof(getName)));
			task.ContinueWith(_ => _childTasks.Remove(task));
		}
	}

	/// <summary>
	/// </summary>
	public void TryAddChildTask(Func<string> getName, ValueTask task)
	{
		if(!task.IsCompleted)
			TryAddChildTask(getName, task.AsTask());
	}

	private MessageQueueItem SelectNextMessage()
	{
		static bool canProcessOverLimit(Message msg) => msg is ISubscriptionMessage { IsSubscribe: false };

		lock (_messages.SyncRoot)
		{
			var isControlProcessing = false;
			var isTransactionProcessing = false;
			var numProcessing = 0;

			foreach (var msg in _messages.Where(m => m.IsProcessing))
			{
				isControlProcessing |= msg.IsControl;
				isTransactionProcessing |= msg.IsTransaction;
				++numProcessing;
			}

			// cant process anything in parallel while connect/disconnect/reset is processing
			if(isControlProcessing)
				return null;

			// if transaction is processing currently, we can process other non-exclusive messages in parallel (marketdata request for example)
			if(isTransactionProcessing)
				return numProcessing >= _adapter.MaxParallelMessages
					? _messages.FirstOrDefault(m => canProcessOverLimit(m.Message)) // can't process more messages because of the limit.
					: _messages.FirstOrDefault(m => !m.IsStartedProcessing && !(m.IsControl || m.IsTransaction));

			return numProcessing >= _adapter.MaxParallelMessages
				? _messages.FirstOrDefault(m => canProcessOverLimit(m.Message) || (!m.IsStartedProcessing && m.IsControl)) // if the limit is exceeded we can only process control messages
				: _messages.FirstOrDefault(m => !m.IsStartedProcessing);
		}
	}

	private void BeginProcessMessage(MessageQueueItem msg, Func<ValueTask> process)
	{
		if(msg.IsStartedProcessing)
			throw new ArgumentException($"processing is already started for {msg.Message}", nameof(msg));

		ValueTask wrapper()
		{
			try
			{
				if (msg.IsCanceled)
				{
					var tcs = AsyncHelper.CreateTaskCompletionSource(false);
					tcs.SetCanceled();
					msg.Task = tcs.Task;

					if(msg.IsTransaction)
						_adapter.HandleMessageException(msg.Message, new OperationCanceledException("canceled"));

					return default;
				}

				var vt = process();

				if (vt.IsCompleted)
				{
					msg.Task = Task.CompletedTask;
					this.AddVerboseLog("endprocess: {0}", msg.Message.Type);
					return vt;
				}

				msg.Task = vt.AsTask();
				if(!msg.IsControl)
					TryAddChildTask(() => $"task({msg.Message})", msg.Task);

				msg.Task.ContinueWith(t =>
				{
					if(!t.IsCompletedSuccessfully)
					{
						Exception ex = t.IsFaulted ? t.Exception : new OperationCanceledException("canceled");
						_adapter.HandleMessageException(msg.Message, ex);
						this.AddVerboseLog("endprocess: {0} ({1})", msg.Message.Type, ex?.GetType().Name);
					}
					else
					{
						this.AddVerboseLog("endprocess: {0} (OK)", msg.Message.Type);
					}

					_processMessageEvt.Set(); // check next message
				});

				return vt;
			}
			catch (Exception e)
			{
				var tcs = AsyncHelper.CreateTaskCompletionSource(false);
				tcs.TrySetFrom(e);
				msg.Task = tcs.Task;

				_ = tcs.Task.Exception; // observe

				throw;
			}
		}

#pragma warning disable CA2012

		AsyncHelper.CatchHandle(
			wrapper,
			handleError:    e => _adapter.HandleMessageException(msg.Message, e),
			handleCancel:   e => _adapter.HandleMessageException(msg.Message, e),
			rethrowCancel:  false,
			rethrowErr:     false
		);

#pragma warning restore CA2012
	}

	private bool BeginProcessNextMessage()
	{
		var msg = SelectNextMessage();

		if(msg == null)
			return false;

		var token = _globalCts.Token;

		BeginProcessMessage(msg, () =>
		{
			this.AddVerboseLog("beginprocess: {0}", msg.Message.Type);

			if(msg.IsControl)
				return msg.Message switch
				{
					ConnectMessage m    => ConnectAsync(m, token),
					DisconnectMessage m => DisconnectAsync(m),
					ResetMessage m      => ResetAsync(m),
					_                   => throw new ArgumentOutOfRangeException(nameof(msg), $"unexpected message {msg.Message.Type}")
				};

			if(!_isConnectionStarted || _isDisconnecting)
				throw new InvalidOperationException($"unable to process {msg.Message.Type} in this state. connStarted={_isConnectionStarted}, disconnecting={_isDisconnecting}");

			return msg.Message switch
			{
				SecurityLookupMessage m    => _adapter.SecurityLookupAsync(m, token),
				PortfolioLookupMessage m   => _adapter.PortfolioLookupAsync(m, token),
				BoardLookupMessage m       => _adapter.BoardLookupAsync(m, token),

				TimeMessage m              => _adapter.TimeMessageAsync(m, token),

				OrderStatusMessage m       => _adapter.OrderStatusAsync(m, token),

				OrderReplaceMessage m      => _adapter.ReplaceOrderAsync(m, token),
				OrderPairReplaceMessage m  => _adapter.ReplaceOrderPairAsync(m, token),
				OrderRegisterMessage m     => _adapter.RegisterOrderAsync(m, token),
				OrderCancelMessage m       => _adapter.CancelOrderAsync(m, token),
				OrderGroupCancelMessage m  => _adapter.CancelOrderGroupAsync(m, token),

				MarketDataMessage m        => _adapter.ProcessMarketDataAsync(m, token),

				_                          => _adapter.ProcessMessageAsync(msg.Message, token)
			};
		});

		return true;
	}

	private async Task ProcessMessagesAsync()
	{
		while (true)
		{
			await _processMessageEvt.WaitAsync();
			if(IsDisposeStarted)
				break;

			_processMessageEvt.Reset();

			lock(_messages.SyncRoot)
				_messages.RemoveWhere(m => m.IsDone);

			try
			{
				while(BeginProcessNextMessage()) {}
			}
			catch (Exception e)
			{
				this.AddErrorLog("error processing message: {0}", e);
			}
		}
	}

	private ValueTask ConnectAsync(ConnectMessage msg, CancellationToken token)
	{
		if(_isConnectionStarted)
			throw new InvalidOperationException(LocalizedStrings.Str1619);

		_isConnectionStarted = true;

		return _adapter.ConnectAsync(msg, token);
	}

	private async ValueTask DisconnectAsync(DisconnectMessage msg)
	{
		if(!_isConnectionStarted)
			throw new InvalidOperationException("not connected");

		if(_isDisconnecting)
			throw new InvalidOperationException("already disconnecting");

		_isDisconnecting = true;

		CancelAndReplaceGlobalCts();

		if(!await WhenChildrenComplete(_adapter.DisconnectTimeout.CreateTimeoutToken()))
			throw new InvalidOperationException("unable to complete disconnect. some tasks are still running.");

		await _adapter.DisconnectAsync(msg, default);

		_isDisconnecting = _isConnectionStarted = false;
	}

	private async ValueTask ResetAsync(ResetMessage msg)
	{
		_isDisconnecting = true;

		// token is already canceled in EnqueueMessage
		await AsyncHelper.CatchHandle(() => WhenChildrenComplete(_adapter.DisconnectTimeout.CreateTimeoutToken()));

		await _adapter.ResetAsync(msg, default); // reset must not throw.

		_isDisconnecting = _isConnectionStarted = false;
	}

	private void CancelAndReplaceGlobalCts()
	{
		_globalCts.Cancel();
		_globalCts = new();
	}

	/// <summary>
	/// Create child cancellation token for transaction id.
	/// </summary>
	public CancellationToken CreateChildTokenByTransId(long transactionId, CancellationToken parentToken)
	{
		var (cts, childToken) = parentToken.CreateChildToken();
		_childTokens.Add(transactionId, cts);
		return childToken;
	}

	/// <summary>
	/// Remove child token.
	/// </summary>
	public void RemoveChildToken(long transactionId) => _childTokens.Remove(transactionId);

	/// <summary>
	/// Cancel child token.
	/// </summary>
	public bool TryCancelChildTokenByTransId(long transactionId)
	{
		var cts = _childTokens.TryGetAndRemove(transactionId);
		if(cts == null)
			return false;

		cts.Cancel();
		return true;
	}

	private async Task<bool> WhenChildrenComplete(CancellationToken token)
	{
		var tasks = _childTasks.CopyAndClear();

		var allComplete = true;

		await Task.WhenAll(tasks.Select(t => t.Key.WithCancellation(token))).CatchHandle(finalizer: () =>
		{
			var incomplete = tasks.Where(t => !t.Key.IsCompleted).Select(t => t.Value()).ToArray();
			if(incomplete.Any())
			{
				allComplete = false;
				this.AddErrorLog("following tasks were not completed:\n" + incomplete.JoinN());
			}
		});

		return allComplete;
	}
}
