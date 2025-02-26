#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.BusinessEntities.BusinessEntities
File: MarketDepthPair.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.BusinessEntities
{
	using System;

	using Ecng.Common;

	using StockSharp.Messages;
	using StockSharp.Localization;

	/// <summary>
	/// Quotes pair.
	/// </summary>
	[System.Runtime.Serialization.DataContract]
	[Serializable]
	public class MarketDepthPair
	{
		private readonly bool _isFull;

		/// <summary>
		/// Initializes a new instance of the <see cref="MarketDepthPair"/>.
		/// </summary>
		/// <param name="bid">Bid.</param>
		/// <param name="ask">Ask.</param>
		public MarketDepthPair(QuoteChange? bid, QuoteChange? ask)
		{
			Bid = bid;
			Ask = ask;

			_isFull = bid != null && ask != null;
		}

		/// <summary>
		/// Bid.
		/// </summary>
		[DisplayNameLoc(LocalizedStrings.BidKey)]
		[DescriptionLoc(LocalizedStrings.Str494Key)]
		public QuoteChange? Bid { get; }

		/// <summary>
		/// Ask.
		/// </summary>
		[DisplayNameLoc(LocalizedStrings.AskKey)]
		[DescriptionLoc(LocalizedStrings.Str495Key)]
		public QuoteChange? Ask { get; }

		/// <summary>
		/// Spread by price. Is <see langword="null" />, if one of the quotes is empty.
		/// </summary>
		[DisplayNameLoc(LocalizedStrings.Str496Key)]
		[DescriptionLoc(LocalizedStrings.Str497Key)]
		public decimal? SpreadPrice => _isFull ? (Ask.Value.Price - Bid.Value.Price) : null;

		/// <summary>
		/// Spread by volume. If negative, it best ask has a greater volume than the best bid. Is <see langword="null" />, if one of the quotes is empty.
		/// </summary>
		[DisplayNameLoc(LocalizedStrings.Str498Key)]
		[DescriptionLoc(LocalizedStrings.Str499Key)]
		public decimal? SpreadVolume => _isFull ? (Ask.Value.Volume - Bid.Value.Volume).Abs() : null;

		/// <summary>
		/// The middle of spread. Is <see langword="null" />, if quotes are empty.
		/// </summary>
		[DisplayNameLoc(LocalizedStrings.SpreadKey)]
		[DescriptionLoc(LocalizedStrings.SpreadMiddleKey, true)]
		public decimal? MiddlePrice => (Bid?.Price).GetSpreadMiddle(Ask?.Price);

		/// <summary>
		/// Quotes pair has <see cref="Bid"/> and <see cref="Ask"/>.
		/// </summary>
		public bool IsFull => _isFull;

		/// <inheritdoc />
		public override string ToString()
		{
			return "{{{0}}} {{{1}}}".Put(Bid, Ask);
		}
	}
}