﻿using System.Data;
using System.Data.Linq;
using System.Threading;
using CommonLib;
using CommonLib.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommonLib.Net;
using System.Net;
using CommonLib.Remoting;
using MySql.Data.MySqlClient;

namespace AccountServer
{
    public class AccountServer : INetworkConnectionFactory
    {
        private static readonly log4net.ILog _Log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        public static AccountServer Instance { get; private set; }

        private bool _Running;

        private readonly PropertyTree _ConfigTree;
        public PropertyTree ConfigTree
        {
            get { return _ConfigTree; }
        }

        private readonly ConnectionListener _Listener;
        public ConnectionListener Listener
        {
            get { return _Listener; }
        }

        private IDbConnection _DatabaseConnection;
        public IDbConnection DatabaseConnection
        {
            get { return _DatabaseConnection; }
        }

        private RemotingServer _RemotingServer;
        public RemotingServer RemotingServer
        {
            get { return _RemotingServer; }
        }

        private readonly List<CharServerService> _CharServers;
        public List<CharServerService> CharServers
        {
            get { return _CharServers; }
        }

        public AccountServer()
        {
            Instance = this;
            _ConfigTree = new PropertyTree(StringComparer.InvariantCultureIgnoreCase);
            _Listener = new ConnectionListener(this);
            _CharServers = new List<CharServerService>();
        }

        public void Run()
        {
            _Log.Info("AccountServer is starting");

            _Log.Info("Reading configuration");
            _ConfigTree.FromConfigFile("Config/AccountServer.cfg");

            _Log.Info("Connecting to database");
            _DatabaseConnection = new MySqlConnection(BuildConnectionString());

            try
            {
                _DatabaseConnection.Open();
            }
            catch (Exception ex)
            {
                _Log.Fatal("Error connecting to database!", ex);
                return;
            }

            _RemotingServer = new RemotingServer(_ConfigTree.Get("inter.uri", "net.tcp://localhost:5401/"));
            _RemotingServer.ExposeType<CharServerService>("CharServer", typeof(ICharServerService));
            _RemotingServer.Start();
            _Log.InfoFormat("Accepting CharServer connections at {0}", _RemotingServer.BaseUri);

            IPAddress bindAddress = IPAddress.Parse(_ConfigTree.Get("network.interface.ip", "0.0.0.0"));
            int bindPort = _ConfigTree.Get("network.interface.port", 5500);
            _Log.InfoFormat("Accepting connections at {0}:{1}", bindAddress, bindPort);
            _Listener.Bind(bindAddress, bindPort);
            _Listener.Listen();

            _Running = true;
            while (_Running)
                Thread.Yield();

            _RemotingServer.Stop();
        }

        public bool Authenticate(string username, string password, int version, byte clientType, out int errorCode, out int accountID, out Sex sex)
        {
            AccountServerDataContext dc = GetDataContext();
            var account = (from acc in dc.Accounts where acc.Username == username select acc).FirstOrDefault();
            
            accountID = 0;
            sex = Sex.Female;

            if (account == null)
            {
                errorCode = 0;
                return false;
            }

            if (account.Password != password)
            {
                errorCode = 1;
                return false;
            }

            if (account.State != 0)
            {
                errorCode = account.State - 1;
                return false;
            }

            if (account.BanTime.HasValue)
            {
                if (account.BanTime.Value > DateTime.Now)
                {
                    errorCode = 6;
                    return false;
                }
                else
                {
                    account.BanTime = null;
                    dc.SubmitChanges();
                }
            }

            accountID = account.AccountID;
            sex = account.Sex;
            errorCode = 0;

            return false;
        }

        public AccountServerDataContext GetDataContext()
        {
            return new AccountServerDataContext(_DatabaseConnection);
        }

        NetworkConnectionBase INetworkConnectionFactory.Create(System.Net.Sockets.Socket client)
        {
            return new AccountServerClient(client);
        }

        private string BuildConnectionString()
        {
            return string.Format("SERVER={0};DATABASE={1};UID={2};PASSWORD={3};",
                                 _ConfigTree.Get("database.server", "127.0.0.1"),
                                 _ConfigTree.Get("database.dbname", "rhea_logindb"),
                                 _ConfigTree.Get("database.username", "rhea"),
                                 _ConfigTree.Get("database.password", "rhea"));
        }

        public bool RegisterCharServer(CharServerService charServer)
        {
            _CharServers.Add(charServer);
            _Log.InfoFormat("CharServer '{0}' connected with address {1}:{2}", charServer.Name, charServer.Address, charServer.Port);

            return true;
        }

        public void UnregisterCharServer(CharServerService charServer)
        {
            _Log.InfoFormat("CharServer '{0}' disconnected", charServer.Name);
            _CharServers.Remove(charServer);
        }
    }
}
