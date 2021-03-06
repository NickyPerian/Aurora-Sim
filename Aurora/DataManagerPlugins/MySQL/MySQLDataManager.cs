/*
 * Copyright (c) Contributors, http://aurora-sim.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using MySql.Data.MySqlClient;
using Aurora.Framework;
using Aurora.DataManager.Migration;
using log4net;

namespace Aurora.DataManager.MySQL
{
    public class MySQLDataLoader : DataManagerBase
    {
        private string m_connectionString = "";
        private static readonly ILog m_log =
                LogManager.GetLogger (
                MethodBase.GetCurrentMethod ().DeclaringType);

        public override string Identifier
        {
            get { return "MySQLData"; }
        }

        public void CloseDatabase(MySqlConnection connection)
        {
            //Interlocked.Decrement (ref m_locked);
            //connection.Close();
            //connection.Dispose();
        }

        public override void CloseDatabase()
        {
            //Interlocked.Decrement (ref m_locked);
            //m_connection.Close();
            //m_connection.Dispose();
        }

        public IDataReader Query(string sql, Dictionary<string, object> parameters)
        {
            try
            {
                MySqlParameter[] param = new MySqlParameter[parameters.Count];
                int i = 0;
                foreach (KeyValuePair<string, object> p in parameters)
                {
                    param[i] = new MySqlParameter(p.Key, p.Value);
                    i++;
                }
                return MySqlHelper.ExecuteReader (m_connectionString, sql, param);
            }
            catch (Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Query(" + sql + "), " + e.ToString ());
                return null;
            }
        }

        public void ExecuteNonQuery(string sql, Dictionary<string, object> parameters)
        {
            try
            {
                MySqlParameter[] param = new MySqlParameter[parameters.Count];
                int i = 0;
                foreach (KeyValuePair<string, object> p in parameters)
                {
                    param[i] = new MySqlParameter (p.Key, p.Value);
                    i++;
                }
                MySqlHelper.ExecuteNonQuery (m_connectionString, sql, param);
            }
            catch (Exception e)
            {
                m_log.Error ("[MySQLDataLoader] ExecuteNonQuery(" + sql + "), " + e.ToString ());
            }
        }

        public override void ConnectToDatabase(string connectionstring, string migratorName, bool validateTables)
        {
            m_connectionString = connectionstring;

            var migrationManager = new MigrationManager (this, migratorName, validateTables);
            migrationManager.DetermineOperation ();
            migrationManager.ExecuteOperation ();
        }

        public override List<string> Query(string keyRow, object keyValue, string table, string wantedValue)
        {
            IDataReader reader = null;
            List<string> retVal = new List<string> ();
            string query;
            if (keyRow == "")
            {
                query = String.Format ("select {0} from {1}",
                                      wantedValue, table);
            }
            else
            {
                query = String.Format ("select {0} from {1} where {2} = '{3}'",
                                      wantedValue, table, keyRow, keyValue.ToString ());
            }
            try
            {
                using (reader = Query (query, new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (reader[i] is byte[])
                                retVal.Add (OpenMetaverse.Utils.BytesToString ((byte[])reader[i]));
                            else
                                retVal.Add (reader.GetString (i));
                        }
                    }
                    return retVal;
                }
            }
            catch (Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                return retVal;
            }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                }
            }
        }

        public override List<string> Query(string whereClause, string table, string wantedValue)
        {
            IDataReader reader = null;
            List<string> retVal = new List<string> ();
            string query = String.Format ("select {0} from {1} where {2}",
                                      wantedValue, table, whereClause);
            try
            {
                using (reader = Query (query, new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (reader[i] != DBNull.Value)
                                retVal.Add (reader.GetString (i));
                        }
                    }
                    return retVal;
                }
            }
            catch (Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                return null;
            }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                }
            }
        }

        public override List<string> QueryFullData(string whereClause, string table, string wantedValue)
        {
            IDataReader reader = null;
            List<string> retVal = new List<string> ();
            string query = String.Format ("select {0} from {1} {2}",
                                         wantedValue, table, whereClause);
            try
            {
                using (reader = Query (query, new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            retVal.Add (reader.GetString (i));
                        }
                    }
                    return retVal;

                }
            }
            catch (Exception e)
            {
                m_log.Error ("[MySQLDataLoader] QueryFullData(" + query + "), " + e.ToString ());
                return null;
            }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                }
            }
        }

        public override IDataReader QueryData(string whereClause, string table, string wantedValue)
        {
            string query = String.Format("select {0} from {1} {2}",
                                      wantedValue, table, whereClause);
            return Query (query, new Dictionary<string, object> ());
        }

        public override List<string> Query(string keyRow, object keyValue, string table, string wantedValue, string order)
        {
            IDataReader reader = null;
            List<string> retVal = new List<string> ();
            string query = "";
            if (keyRow == "")
            {
                query = String.Format ("select {0} from {1}",
                                      wantedValue, table);
            }
            else
            {
                query = String.Format ("select {0} from {1} where {2} = '{3}'",
                                      wantedValue, table, keyRow, keyValue);
            }
            try
            {
                using (reader = Query (query + order, new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            Type r = reader[i].GetType ();
                            retVal.Add (r == typeof (DBNull) ? null : reader.GetString (i));
                        }
                    }
                    return retVal;
                }
            }
            catch (Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                return new List<string> ();
            }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                }
            }
        }

        public override List<string> Query(string[] keyRow, object[] keyValue, string table, string wantedValue)
        {
            IDataReader reader = null;
            List<string> retVal = new List<string> ();
            string query = String.Format ("select {0} from {1} where ",
                                      wantedValue, table);
            int i = 0;
            foreach (object value in keyValue)
            {
                query += String.Format ("{0} = '{1}' and ", keyRow[i], value);
                i++;
            }
            query = query.Remove (query.Length - 5);

            try
            {
                using (reader = Query (query, new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (i = 0; i < reader.FieldCount; i++)
                        {
                            Type r = reader[i].GetType ();
                            retVal.Add (r == typeof (DBNull) ? null : reader.GetString (i));
                        }
                    }
                    return retVal;
                }
            }
            catch(Exception e)
            {
                m_log.Error("[MySQLDataLoader] Query(" + query + "), " + e.ToString());
                return null;
            }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error ("[MySQLDataLoader] Query(" + query + "), " + e.ToString ());
                }
            }
        }

        public override Dictionary<string, List<string>> QueryNames(string[] keyRow, object[] keyValue, string table, string wantedValue)
        {
            IDataReader reader = null;
            Dictionary<string, List<string>> retVal = new Dictionary<string, List<string>> ();
            string query = String.Format ("select {0} from {1} where ",
                                      wantedValue, table);
            int i = 0;
            foreach (object value in keyValue)
            {
                query += String.Format ("{0} = '{1}' and ", keyRow[i], value);
                i++;
            }
            query = query.Remove (query.Length - 5);

            try
            {
                using (reader = Query (query, new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (i = 0; i < reader.FieldCount; i++)
                        {
                            Type r = reader[i].GetType ();
                            AddValueToList (ref retVal, reader.GetName (i),
                                           r == typeof (DBNull) ? null : reader[i].ToString ());
                        }
                    }
                    return retVal;
                }
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] QueryNames(" + query + "), " + e.ToString ());
                return null;
            }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error ("[MySQLDataLoader] QueryNames(" + query + "), " + e.ToString ());
                }
            }
        }

        private void AddValueToList (ref Dictionary<string, List<string>> dic, string key, string value)
        {
            if (!dic.ContainsKey (key))
                dic.Add (key, new List<string> ());

            dic[key].Add (value);
        }

        public override bool Update(string table, object[] setValues, string[] setRows, string[] keyRows, object[] keyValues)
        {
            string query = String.Format("update {0} set ", table);
            int i = 0;
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            foreach (object value in setValues)
            {
                query += string.Format("{0} = ?{1},", setRows[i], setRows[i]);
                string valueSTR = value.ToString();
                if(valueSTR == "")
                    valueSTR = " ";
                parameters["?" + setRows[i]] = valueSTR;
                i++;
            }
            i = 0;
            query = query.Remove(query.Length - 1);
            query += " where ";
            foreach (object value in keyValues)
            {
                query += String.Format("{0}  = '{1}' and ", keyRows[i], value);
                i++;
            }
            query = query.Remove(query.Length - 5);
            try
            {
                ExecuteNonQuery (query, parameters);
            }
            catch (MySqlException e)
            {
                m_log.Error ("[MySQLDataLoader] Update(" + query + "), " + e.ToString ());
            }
            return true;
        }

        public override bool DirectUpdate (string table, object[] setValues, string[] setRows, string[] keyRows, object[] keyValues)
        {
            string query = String.Format("update {0} set ", table);
            int i = 0;
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            foreach(object value in setValues)
            {
                string valueSTR = value.ToString();
                query += string.Format("{0} = {1},", setRows[i], valueSTR);
                i++;
            }
            i = 0;
            query = query.Remove(query.Length - 1);
            query += " where ";
            foreach(object value in keyValues)
            {
                query += String.Format("{0}  = '{1}' and ", keyRows[i], value);
                i++;
            }
            query = query.Remove(query.Length - 5);
            try
            {
                ExecuteNonQuery(query, parameters);
            }
            catch(MySqlException e)
            {
                m_log.Error("[MySQLDataLoader] Update(" + query + "), " + e.ToString());
            }
            return true;
        }

        public override bool Insert(string table, object[] values)
        {
            string query = String.Format("insert into {0} values (", table);
            query = values.Aggregate(query, (current, value) => current + String.Format("'{0}',", value));
            query = query.Remove(query.Length - 1);
            query += ")";

            try
            {
                ExecuteNonQuery (query, new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Insert(" + query + "), " + e.ToString ());
            }
            return true;
        }

        public override bool Insert(string table, string[] keys, object[] values)
        {
            string query = String.Format("insert into {0} (", table);
            Dictionary<string, object> param = new Dictionary<string, object>();

            int i = 0;
            foreach (string key in keys)
            {
                param.Add("?" + key, values[i]);
                query += String.Format("{0},", key);
                i++;
            }
            query = query.Remove(query.Length - 1);
            query += ") values (";

            query = keys.Aggregate(query, (current, key) => current + String.Format("?{0},", key));
            query = query.Remove(query.Length - 1);
            query += ")";

            try
            {
                ExecuteNonQuery (query, param);
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Insert(" + query + "), " + e.ToString ());
            }
            return true;
        }

        public override bool Replace(string table, string[] keys, object[] values)
        {
            string query = String.Format("replace into {0} (", table);
            Dictionary<string, object> param = new Dictionary<string, object>();

            int i = 0;
            foreach (string key in keys)
            {
                string kkey = key;
                if (key.Contains('`'))
                    kkey = key.Replace("`", ""); //Remove them

                param.Add("?" + kkey, values[i].ToString());
                query += "`" + kkey + "`" + ",";
                i++;
            }
            query = query.Remove(query.Length - 1);
            query += ") values (";

            foreach (string key in keys)
            {
                string kkey = key;
                if (key.Contains('`'))
                    kkey = key.Replace("`", ""); //Remove them
                query += String.Format("?{0},", kkey);
            }
            query = query.Remove(query.Length - 1);
            query += ")";

            try
            {
                ExecuteNonQuery (query, param);
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Replace(" + query + "), " + e.ToString ());
                return false;
            }
            return true;
        }

        public override bool DirectReplace(string table, string[] keys, object[] values)
        {
            string query = String.Format("replace into {0} (", table);
            Dictionary<string, object> param = new Dictionary<string, object>();

            foreach (string key in keys)
            {
                string kkey = key;
                if (key.Contains('`'))
                    kkey = key.Replace("`", ""); //Remove them

                query += "`" + kkey + "`" + ",";
            }
            query = query.Remove(query.Length - 1);
            query += ") values (";

            query = values.Aggregate(query, (current, key) => current + String.Format("{0},", key.ToString()));
            query = query.Remove(query.Length - 1);
            query += ")";

            try
            {
                ExecuteNonQuery (query, param);
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] DirectReplace(" + query + "), " + e.ToString ());
                return false;
            }
            return true;
        }

        public override bool Insert(string table, object[] values, string updateKey, object updateValue)
        {
            string query = String.Format("insert into {0} VALUES('", table);
            query = values.Aggregate(query, (current, value) => current + (value + "','"));
            query = query.Remove(query.Length - 2);
            query += String.Format(") ON DUPLICATE KEY UPDATE {0} = '{1}'", updateKey, updateValue);
            try
            {
                ExecuteNonQuery (query, new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Insert(" + query + "), " + e.ToString ());
                return false;
            }
            return true;
        }

        public override bool Delete(string table, string[] keys, object[] values)
        {
            string query = "delete from " + table + (keys.Length > 0 ? " WHERE " : "");
            int i = 0;
            foreach (object value in values)
            {
                query += keys[i] + " = '" + value + "' AND ";
                i++;
            }
            if(keys.Length > 0)
                query = query.Remove(query.Length - 5);
            try
            {
                ExecuteNonQuery (query, new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Delete(" + query + "), " + e.ToString ());
                return false;
            }
            return true;
        }

        public override string FormatDateTimeString(int time)
        {
            if (time == 0)
                return "now()";
            return "date_add(now(), interval " + time + " minute)";
        }

        public override string IsNull(string field, string defaultValue)
        {
            return "IFNULL(" + field + "," + defaultValue + ")";
        }

        public override string ConCat(string[] toConcat)
        {
            string returnValue = toConcat.Aggregate("concat(", (current, s) => current + (s + ","));
            return returnValue.Substring(0, returnValue.Length - 1) + ")";
        }

        public override bool Delete(string table, string whereclause)
        {
            string query = "delete from " + table + " WHERE " + whereclause;
            try
            {
                ExecuteNonQuery (query, new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] Delete", e);
                return false;
            }
            return true;
        }

        public override bool DeleteByTime(string table, string key)
        {
            string query = "delete from " + table + " WHERE '" + key + "' < now()";
            try
            {
                ExecuteNonQuery (query, new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] DeleteByTime", e);
                return false;
            }
            return true;
        }

        public override void CreateTable(string table, ColumnDefinition[] columns)
        {
            table = table.ToLower ();
            if (TableExists (table))
            {
                throw new DataManagerException ("Trying to create a table with name of one that already exists.");
            }

            string columnDefinition = string.Empty;
            var primaryColumns = (from cd in columns where cd.IsPrimary select cd);
            bool multiplePrimary = primaryColumns.Count () > 1;

            foreach (ColumnDefinition column in columns)
            {
                if (columnDefinition != string.Empty)
                {
                    columnDefinition += ", ";
                }
                columnDefinition += "`" + column.Name + "` " + GetColumnTypeStringSymbol (column.Type) + ((column.IsPrimary && !multiplePrimary) ? " PRIMARY KEY" : string.Empty);
            }

            string multiplePrimaryString = string.Empty;
            if (multiplePrimary)
            {
                string listOfPrimaryNamesString = string.Empty;
                foreach (ColumnDefinition column in primaryColumns)
                {
                    if (listOfPrimaryNamesString != string.Empty)
                    {
                        listOfPrimaryNamesString += ", ";
                    }
                    listOfPrimaryNamesString += "`" + column.Name + "`";
                }
                multiplePrimaryString = string.Format (", PRIMARY KEY ({0}) ", listOfPrimaryNamesString);
            }

            string query = string.Format ("create table " + table + " ( {0} {1}) ", columnDefinition, multiplePrimaryString);

            try
            {
                ExecuteNonQuery (query, new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] CreateTable", e);
            }
        }

        public override void UpdateTable (string table, ColumnDefinition[] columns, Dictionary<string, string> renameColumns)
        {
            table = table.ToLower ();
            if (!TableExists (table))
            {
                throw new DataManagerException ("Trying to update a table with name of one that does not exist.");
            }

            List<ColumnDefinition> oldColumns = ExtractColumnsFromTable (table);

            Dictionary<string, ColumnDefinition> removedColumns = new Dictionary<string, ColumnDefinition> ();
            Dictionary<string, ColumnDefinition> modifiedColumns = new Dictionary<string, ColumnDefinition> ();
            Dictionary<string, ColumnDefinition> addedColumns = columns.Where (column => !oldColumns.Contains (column)).ToDictionary (column => column.Name.ToLower());
            foreach (ColumnDefinition column in oldColumns.Where (column => !columns.Contains (column)))
            {
                if(addedColumns.ContainsKey(column.Name.ToLower()))
                {
                    if(column.Name.ToLower() != addedColumns[column.Name.ToLower()].Name.ToLower() ||
                        column.Type != addedColumns[column.Name.ToLower()].Type)
                        modifiedColumns.Add(column.Name.ToLower(), addedColumns[column.Name.ToLower()]);
                    addedColumns.Remove(column.Name.ToLower());
                }
                else
                    removedColumns.Add(column.Name.ToLower(), column);
            }

            try
            {
                foreach (ColumnDefinition column in addedColumns.Values)
                {
                    string addedColumnsQuery = "add `" + column.Name + "` " + GetColumnTypeStringSymbol (column.Type) + " ";
                    string query = string.Format ("alter table " + table + " " + addedColumnsQuery);

                    ExecuteNonQuery (query, new Dictionary<string, object> ());
                }
                foreach (ColumnDefinition column in modifiedColumns.Values)
                {
                    string modifiedColumnsQuery = "modify column `" + column.Name + "` " + GetColumnTypeStringSymbol (column.Type) + " ";
                    string query = string.Format ("alter table " + table + " " + modifiedColumnsQuery);

                    ExecuteNonQuery (query, new Dictionary<string, object> ());
                }
                foreach (ColumnDefinition column in removedColumns.Values)
                {
                    string droppedColumnsQuery = "drop `" + column.Name + "` ";
                    string query = string.Format ("alter table " + table + " " + droppedColumnsQuery);

                    ExecuteNonQuery (query, new Dictionary<string, object> ());
                }
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] UpdateTable", e);
            }
        }

        public override string GetColumnTypeStringSymbol(ColumnTypes type)
        {
            switch (type)
            {
                case ColumnTypes.Double:
                    return "DOUBLE";
                case ColumnTypes.Integer11:
                    return "int(11)";
                case ColumnTypes.Integer30:
                    return "int(30)";
                case ColumnTypes.Char36:
                    return "char(36)";
                case ColumnTypes.Char32:
                    return "char(32)";
                case ColumnTypes.String:
                    return "TEXT";
                case ColumnTypes.String1:
                    return "VARCHAR(1)";
                case ColumnTypes.String2:
                    return "VARCHAR(2)";
                case ColumnTypes.String16:
                    return "VARCHAR(16)";
                case ColumnTypes.String32:
                    return "VARCHAR(32)";
                case ColumnTypes.String36:
                    return "VARCHAR(36)";
                case ColumnTypes.String45:
                    return "VARCHAR(45)";
                case ColumnTypes.String50:
                    return "VARCHAR(50)";
                case ColumnTypes.String64:
                    return "VARCHAR(64)";
                case ColumnTypes.String128:
                    return "VARCHAR(128)";
                case ColumnTypes.String100:
                    return "VARCHAR(100)";
                case ColumnTypes.String255:
                    return "VARCHAR(255)";
                case ColumnTypes.String512:
                    return "VARCHAR(512)";
                case ColumnTypes.String1024:
                    return "VARCHAR(1024)";
                case ColumnTypes.String8196:
                    return "VARCHAR(8196)";
                case ColumnTypes.Text:
                    return "TEXT";
                case ColumnTypes.MediumText:
                    return "MEDIUMTEXT";
                case ColumnTypes.LongText:
                    return "LONGTEXT";
                case ColumnTypes.Blob:
                    return "blob";
                case ColumnTypes.LongBlob:
                    return "longblob";
                case ColumnTypes.Date:
                    return "DATE";
                case ColumnTypes.DateTime:
                    return "DATETIME";
                case ColumnTypes.TinyInt1:
                    return "TINYINT(1)";
                case ColumnTypes.TinyInt4:
                    return "TINYINT(4)";
                default:
                    throw new DataManagerException("Unknown column type.");
            }
        }

        public override void DropTable(string tableName)
        {
            tableName = tableName.ToLower ();
            try
            {
                ExecuteNonQuery (string.Format ("drop table {0}", tableName), new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] DropTable", e);
            }
        }

        public override void ForceRenameTable(string oldTableName, string newTableName)
        {
            newTableName = newTableName.ToLower ();
            try
            {
                ExecuteNonQuery (string.Format ("RENAME TABLE {0} TO {1}", oldTableName, newTableName), new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] ForceRenameTable", e);                
            }
        }

        protected override void CopyAllDataBetweenMatchingTables(string sourceTableName, string destinationTableName, ColumnDefinition[] columnDefinitions)
        {
            sourceTableName = sourceTableName.ToLower ();
            destinationTableName = destinationTableName.ToLower ();
            try
            {
                ExecuteNonQuery (string.Format ("insert into {0} select * from {1}", destinationTableName,
                                                          sourceTableName), new Dictionary<string, object> ());
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] CopyAllDataBetweenMatchingTables", e);
            }
        }

        public override bool TableExists(string table)
        {
            IDataReader reader = null;
            List<string> retVal = new List<string> ();
            try
            {
                using (reader = Query ("show tables", new Dictionary<string, object> ()))
                {
                    while (reader.Read ())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            retVal.Add (reader.GetString (i).ToLower());
                        }
                    }
                }
            }
            catch (Exception e) { m_log.Error("[MySQLDataLoader] TableExists", e); }
            finally
            {
                try
                {
                    if (reader != null)
                    {
                        reader.Close ();
                        //reader.Dispose ();
                    }
                }
                catch (Exception e) { m_log.Error ("[MySQLDataLoader] TableExists", e); }
            }
            return retVal.Contains (table.ToLower());
        }

        protected override List<ColumnDefinition> ExtractColumnsFromTable(string tableName)
        {
            var defs = new List<ColumnDefinition>();
            tableName = tableName.ToLower();
            IDataReader rdr = null;
            try
            {
                rdr = Query(string.Format ("desc {0}", tableName), new Dictionary<string,object>());
                while (rdr.Read ())
                {
                    var name = rdr["Field"];
                    var pk = rdr["Key"];
                    var type = rdr["Type"];
                    defs.Add (new ColumnDefinition { Name = name.ToString (), IsPrimary = pk.ToString () == "PRI", Type = ConvertTypeToColumnType (type.ToString ()) });
                }
            }
            catch(Exception e)
            {
                m_log.Error ("[MySQLDataLoader] ExtractColumnsFromTable", e);
            }
            finally
            {
                try
                {
                    if (rdr != null)
                    {
                        rdr.Close ();
                        //rdr.Dispose ();
                    }
                }
                catch (Exception e) { m_log.Debug("[MySQLDataLoader] ExtractColumnsFromTable", e); }
            }
            return defs;
        }

        private ColumnTypes ConvertTypeToColumnType(string typeString)
        {
            string tStr = typeString.ToLower();
            //we'll base our names on lowercase
            switch (tStr)
            {
                case "double":
                    return ColumnTypes.Double;
                case "int(11)":
                    return ColumnTypes.Integer11;
                case "int(30)":
                    return ColumnTypes.Integer30;
                case "integer":
                    return ColumnTypes.Integer11;
                case "char(36)":
                    return ColumnTypes.Char36;
                case "char(32)":
                    return ColumnTypes.Char32;
                case "varchar(1)":
                    return ColumnTypes.String1;
                case "varchar(2)":
                    return ColumnTypes.String2;
                case "varchar(16)":
                    return ColumnTypes.String16;
                case "varchar(32)":
                    return ColumnTypes.String32;
                case "varchar(36)":
                    return ColumnTypes.String36;
                case "varchar(45)":
                    return ColumnTypes.String45;
                case "varchar(50)":
                    return ColumnTypes.String50;
                case "varchar(64)":
                    return ColumnTypes.String64;
                case "varchar(128)":
                    return ColumnTypes.String128;
                case "varchar(100)":
                    return ColumnTypes.String100;
                case "varchar(255)":
                    return ColumnTypes.String255;
                case "varchar(512)":
                    return ColumnTypes.String512;
                case "varchar(1024)":
                    return ColumnTypes.String1024;
                case "date":
                    return ColumnTypes.Date;
                case "datetime":
                    return ColumnTypes.DateTime;
                case "varchar(8196)":
                    return ColumnTypes.String8196;
                case "text":
                    return ColumnTypes.Text;
                case "mediumtext":
                    return ColumnTypes.MediumText;
                case "longtext":
                    return ColumnTypes.LongText;
                case "blob":
                    return ColumnTypes.Blob;
                case "longblob":
                    return ColumnTypes.LongBlob;
                case "smallint(6)":
                    return ColumnTypes.Integer11;
                case "int(10)":
                    return ColumnTypes.Integer11;
                case "tinyint(1)":
                    return ColumnTypes.TinyInt1;
                case "tinyint(4)":
                    return ColumnTypes.TinyInt4;
            }
            if (tStr.StartsWith ("varchar"))
            {
                //... Someone was editing the database
                // Swallow the exception... but set it to the highest setting so we don't break anything
                return ColumnTypes.String8196;
            }
            if (tStr.StartsWith ("int"))
            {
                //... Someone was editing the database
                // Swallow the exception... but set it to the highest setting so we don't break anything
                return ColumnTypes.Integer11;
            }
            throw new Exception("You've discovered some type in MySQL that's not reconized by Aurora, please place the correct conversion in ConvertTypeToColumnType. Type: " + tStr);
        }

        public override IGenericData Copy()
        {
            return new MySQLDataLoader();
        }
    }
}

