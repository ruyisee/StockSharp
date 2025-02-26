#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Statistics.Algo
File: IStatisticParameter.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Algo.Statistics
{
	using System;

	using Ecng.ComponentModel;
	using Ecng.Serialization;
	using Ecng.Common;

	using StockSharp.Messages;

	/// <summary>
	/// The interface, describing statistic parameter.
	/// </summary>
	public interface IStatisticParameter : IPersistable
	{
		/// <summary>
		/// Parameter name.
		/// </summary>
		string Name { get; }

		/// <summary>
		/// Type.
		/// </summary>
		StatisticParameterTypes Type { get; }

		/// <summary>
		/// The current value of the parameter.
		/// </summary>
		object Value { get; }

		/// <summary>
		/// The displayed parameter name.
		/// </summary>
		string DisplayName { get; }

		/// <summary>
		/// The parameter description.
		/// </summary>
		string Description { get; }

		/// <summary>
		/// Category.
		/// </summary>
		string Category { get; }

		/// <summary>
		/// <see cref="Value"/> change event.
		/// </summary>
		event Action ValueChanged;

		/// <summary>
		/// To reset the parameter value.
		/// </summary>
		void Reset();
	}

	/// <summary>
	/// The interface, describing statistic parameter.
	/// </summary>
	/// <typeparam name="TValue">The type of the parameter value.</typeparam>
	public interface IStatisticParameter<TValue> : IStatisticParameter
	{
		/// <summary>
		/// The current value of the parameter.
		/// </summary>
		new TValue Value { get; }
	}

	/// <summary>
	/// The base statistics parameter.
	/// </summary>
	/// <typeparam name="TValue">The type of the parameter value.</typeparam>
	public abstract class BaseStatisticParameter<TValue> : NotifiableObject, IStatisticParameter<TValue>
		where TValue : IComparable<TValue>
	{
		/// <summary>
		/// Initialize <see cref="BaseStatisticParameter{T}"/>.
		/// </summary>
		/// <param name="type"><see cref="IStatisticParameter.Type"/></param>
		protected BaseStatisticParameter(StatisticParameterTypes type)
		{
			Type = type;

			var type2 = GetType();
			Name = type2.Name.Remove("Parameter");

			DisplayName = type2.GetDisplayName(GetReadableName(Name));
			Description = type2.GetDescription(DisplayName);
			Category = type2.GetCategory();
		}

		/// <inheritdoc />
		public string Name { get; }

		/// <inheritdoc />
		public StatisticParameterTypes Type { get; }

		/// <inheritdoc />
		public string DisplayName { get; }

		/// <inheritdoc />
		public string Description { get; }

		/// <inheritdoc />
		public string Category { get; }

		private TValue _value;

		/// <inheritdoc />
		public virtual TValue Value
		{
			get => _value;
			protected set
			{
				if (_value.CompareTo(value) == 0)
					return;

				_value = value;
				RaiseValueChanged();
			}
		}

		private static string GetReadableName(string name)
		{
			var index = 1;

			while (index < (name.Length - 1))
			{
				if (char.IsUpper(name[index]))
				{
					name = name.Insert(index, " ");
					index += 2;
				}
				else
					index++;
			}

			return name;
		}

		object IStatisticParameter.Value => Value;

		/// <inheritdoc />
		public event Action ValueChanged;

		/// <inheritdoc />
		public virtual void Reset()
		{
			Value = default;
		}

		/// <summary>
		/// To call the event <see cref="ValueChanged"/>.
		/// </summary>
		private void RaiseValueChanged()
		{
			ValueChanged?.Invoke();
			this.Notify(nameof(Value));
		}

		/// <summary>
		/// To load the state of statistic parameter.
		/// </summary>
		/// <param name="storage">Storage.</param>
		public virtual void Load(SettingsStorage storage)
		{
			Value = storage.GetValue(nameof(Value), default(TValue));
		}
	
		/// <summary>
		/// To save the state of statistic parameter.
		/// </summary>
		/// <param name="storage">Storage.</param>
		public virtual void Save(SettingsStorage storage)
		{
			storage.SetValue(nameof(Value), Value);
		}
	}
}