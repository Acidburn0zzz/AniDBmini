﻿
#region Using Statements

using System;
using System.ComponentModel;
using System.Data.SQLite;

#endregion Using Statements

namespace AniDBmini.Collections
{
    public class FileEntry : INotifyPropertyChanged
    {

        #region Properties

        public int fid { get; set; }
        public int lid { get; set; }
        public int eid { get; set; }

        public double length { get; set; }
        public double size { get; set; }

        public string epno { get; set; }
        public string ed2k { get; set; }
        public string source { get; set; }
        public string acodec { get; set; }
        public string vcodec { get; set; }
        public string vres { get; set; }

        public UnixTimestamp watcheddate { get; set; }
        public GroupEntry Group { get; set; }

        private string _path;
        public string path {
            get { return _path; }
            set
            {
                if (value != _path)
                {
                    _path = value;
                    NotifyPropertyChanged("path");
                }
            }
        }

        /// <summary>
        /// <para>0 - unknown - state is unknown or the user doesn't want to provide this information</para>
        /// <para>1 - on hdd - the file is stored on hdd (but is not shared)</para>
        /// <para>2 - on cd - the file is stored on cd</para>
        /// <para>3 - deleted - the file has been deleted or is not available for other reasons (i.e. reencoded)</para>
        /// </summary>
        public int state;

        public string stateIMG
        {
            get
            {
                string str = @"Resources/";
                switch (state)
                {
                    case 1:
                        if (System.IO.File.Exists(path)) str += "onhdd";
                        else goto default;
                        break;
                    case 3:
                        str += "deleted";
                        break;
                    default:
                        str += "unknown";
                        break;
                }

                return str + ".gif";
            }
        }

        public bool watched { get; set; }
        public bool generic { get; set; }

        #endregion Properties

        #region Constructors

        public FileEntry() { }

        /// <summary>
        /// Used for an entry that is to be inserted into the database
        /// from a recently hashed HashItem.
        /// </summary>
        public FileEntry(HashItem item)
        {
            ed2k = item.Hash;
            size = item.Size;

            if (item.State == 1)
                path = item.Path;

            watched = item.Watched;
            generic = false;
        }

        public FileEntry(SQLiteDataReader reader)
        {
            fid = int.Parse(reader["fid"].ToString());
            lid = int.Parse(reader["lid"].ToString());
            eid = int.Parse(reader["eid"].ToString());

            if ((epno = reader["spl_epno"].ToString().FormatNullable()) == null)
                epno = reader["epno"].ToString();

            generic = !string.IsNullOrEmpty(reader["generic"].ToString());

            if (!generic)
            {
                if (!string.IsNullOrWhiteSpace(reader["gid"].ToString()))
                {
                    Group = new GroupEntry
                    {
                        gid = int.Parse(reader["gid"].ToString()),
                        group_name = reader["group_name"].ToString(),
                        group_abbr = reader["group_abbr"].ToString()
                    };
                }

                ed2k = reader["ed2k"].ToString();
                length = double.Parse(reader["length"].ToString());
                size = double.Parse(reader["size"].ToString());

                source = reader["source"].ToString();
                acodec = reader["acodec"].ToString();
                vcodec = reader["vcodec"].ToString();
                vres = reader["vres"].ToString();

                path =  !string.IsNullOrEmpty(reader["path"].ToString()) ? reader["path"].ToString() : null;
            }

            state = int.Parse(reader["state"].ToString());

            if (!string.IsNullOrEmpty(reader["watcheddate"].ToString()))
                watcheddate = double.Parse(reader["watcheddate"].ToString());

            watched = watcheddate != null;
        }

        #endregion Constructors

        #region INotifyPropertyChanged

        /// <summary>
        /// event for INotifyPropertyChanged.PropertyChanged
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// raise the PropertyChanged event
        /// </summary>
        /// <param name="propName"></param>
        protected void NotifyPropertyChanged(string propName)
        {
            if (this.PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propName));
            }
        }

        #endregion

    }
}
