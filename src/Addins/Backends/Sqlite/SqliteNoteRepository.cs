//
// SqliteNoteRepository.cs
//
// Author:
//       Antonius Riha <antoniusriha@gmail.com>
//
// Copyright (c) 2013 Antonius Riha
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using Mono.Data.Sqlite;

namespace Tasque.Data.Sqlite
{
	public class SqliteNoteRepository : INoteRepository
	{
		public SqliteNoteRepository (Database database)
		{
			if (database == null)
				throw new ArgumentNullException ("database");
			this.database = database;
		}

		string INoteRepository.UpdateTitle (INoteCore note, string title)
		{
			var command = "UPDATE Notes SET Name=@name WHERE ID=@id";
			using (var cmd = new SqliteCommand (database.Connection)) {
				cmd.CommandText = command;
				cmd.Parameters.AddWithValue ("@name", title);
				cmd.Parameters.AddIdParameter (note);
				cmd.ExecuteNonQuery ();
			}
			return title;
		}

		string INoteRepository.UpdateText (INoteCore note, string text)
		{
			var command = "UPDATE Notes SET Text=@text WHERE ID=@id";
			using (var cmd = new SqliteCommand (database.Connection)) {
				cmd.CommandText = command;
				cmd.Parameters.AddWithValue ("@text", text);
				cmd.Parameters.AddIdParameter (note);
				cmd.ExecuteNonQuery ();
			}
			return text;
		}

		Database database;
	}
}
