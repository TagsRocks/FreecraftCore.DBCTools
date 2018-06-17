﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using FreecraftCore.Serializer;
using Nito.AsyncEx;

namespace FreecraftCore
{
	/// <summary>
	/// DBC file/stream reader.
	/// Providing ability to read/parse a DBC file from a stream.
	/// </summary>
	/// <typeparam name="TDBCEntryType">The entry type.</typeparam>
	public sealed class DBCReader<TDBCEntryType>
		where TDBCEntryType : IDBCEntryIdentifiable
	{
		private readonly AsyncLock SyncObj = new AsyncLock();

		/// <summary>
		/// The stream that contains the DBC data.
		/// </summary>
		private Stream DBCStream { get; }

		//TODO: We should share a univseral serializer for performance reasons.
		/// <summary>
		/// The serializer
		/// </summary>
		private static ISerializerService Serializer { get; } = new SerializerService();

		static DBCReader()
		{
			Serializer.RegisterType<DBCHeader>();
			Serializer.RegisterType<StringDBC>();
			Serializer.RegisterType<TDBCEntryType>();
			Serializer.Compile();
		}

		public DBCReader(Stream dbcStream)
		{
			if(dbcStream == null) throw new ArgumentNullException(nameof(dbcStream));

			DBCStream = dbcStream;
		}

		public async Task<ParsedDBCFile<TDBCEntryType>> ParseDBCFile()
		{
			//Read the header, it contains some information needed to read the whole DBC
			DBCHeader header = await Serializer.DeserializeAsync<DBCHeader>(new DefaultStreamReaderStrategyAsync(DBCStream));

			//The below is from the: https://github.com/TrinityCore/SpellWork/blob/master/SpellWork/DBC/DBCReader.cs
			if(!header.IsDBC)
				throw new InvalidOperationException($"Failed to load DBC for DBC Type: {typeof(TDBCEntryType)} Signature: {header.Signature}");

			ConfiguredTaskAwaitable<Dictionary<uint, TDBCEntryType>> dbcEntry = ReadDBCEntryBlock(header)
				.ConfigureAwait(false);

			//TODO: Implement DBC string reading
			return new ParsedDBCFile<TDBCEntryType>(await dbcEntry, await ReadDBCStringBlock(header));
		}

		private async Task<Dictionary<uint, TDBCEntryType>> ReadDBCEntryBlock(DBCHeader header)
		{
			//Guessing the size here, no way to know.
			Dictionary<uint, TDBCEntryType> entryMap = new Dictionary<uint, TDBCEntryType>(header.RecordsCount);

			byte[] bytes = new byte[header.RecordSize * header.RecordsCount];

			//Lock for safety, we don't want anyone else accessing the stream while we read it.
			await ReadBytesIntoArrayFromStream(bytes);

			DefaultStreamReaderStrategy reader = new DefaultStreamReaderStrategy(bytes);

			for(int i = 0; i < header.RecordsCount; i++)
			{
				TDBCEntryType entry = Serializer.Deserialize<TDBCEntryType>(reader);

				entryMap.Add(entry.EntryId, entry);
			}

			return entryMap;
		}

		private async Task ReadBytesIntoArrayFromStream(byte[] bytes)
		{
			using(await SyncObj.LockAsync())
				for(int offset = 0; offset < bytes.Length;)
				{
					offset += await DBCStream.ReadAsync(bytes, offset, bytes.Length - offset);
				}
		}

		private async Task<Dictionary<uint, string>> ReadDBCStringBlock(DBCHeader header)
		{
			Dictionary<uint, string> stringMap = new Dictionary<uint, string>(1000);
			DBCStream.Position = header.StartStringPosition;
			byte[] bytes = new byte[DBCStream.Length - DBCStream.Position];

			await ReadBytesIntoArrayFromStream(bytes);
			DefaultStreamReaderStrategyAsync stringReader = new DefaultStreamReaderStrategyAsync(bytes);

			for(int currentOffset = 0; currentOffset < bytes.Length;)
			{
				string readString = (await Serializer.DeserializeAsync<StringDBC>(stringReader)).StringValue;

				stringMap.Add((uint)currentOffset, readString);

				//We must move the offset forward length + null terminator
				currentOffset += readString.Length + 1;
			}

			return stringMap;
		}
	}
}