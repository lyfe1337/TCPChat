﻿using Engine.Model.Server;
using Engine.Network.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace Engine.Network
{
  public sealed class AsyncServer :
    MarshalByRefObject,
    IDisposable
  {
    #region const
    private const int ListenConnections = 100;
    private const int SystemTimerInterval = 1000;
    public const int MaxDataSize = 2 * 1024 * 1024; // 2 Мб
    #endregion

    #region fields
    private Dictionary<string, ServerConnection> connections;
    private Socket listener;
    private P2PService p2pService;
    private ServerRequestQueue requestQueue;
    private bool isServerRunning;
    private long lastTempId;

    private object timerSync = new object();
    private Timer systemTimer;
    #endregion

    #region properties and events
    /// <summary>
    /// Возвращает true если сервер запущен.
    /// </summary>
    public bool IsServerRunning
    {
      get
      {
        ThrowIfDisposed();
        return isServerRunning;
      }
    }

    /// <summary>
    /// Сервис использующийся для прямого соединения пользователей.
    /// </summary>
    public P2PService P2PService
    {
      get
      {
        ThrowIfDisposed();
        return p2pService;
      }
    }

    /// <summary>
    /// Использует ли сервер IPv6 адреса. Если нет, используется IPv4.
    /// </summary>
    public bool UsingIPv6
    {
      get { return listener.AddressFamily == AddressFamily.InterNetworkV6; }
    }
    #endregion

    #region constructors
    public AsyncServer()
    {
      connections = new Dictionary<string, ServerConnection>();
      requestQueue = new ServerRequestQueue();
      isServerRunning = false;
    }
    #endregion

    #region public methods
    /// <summary>
    /// Включает сервер.
    /// </summary>
    /// <param name="serverPort">TCP порт для соединение с сервером.</param>
    /// <param name="p2pServicePort">Порт UDP P2P сервиса.</param>
    /// <param name="usingIPv6">Использовать ли IPv6, при ложном значении будет использован IPv4.</param>
    /// <exception cref="System.ArgumentException"/>
    public void Start(int serverPort, int p2pServicePort, bool usingIPv6)
    {
      ThrowIfDisposed();

      if (isServerRunning)
        return;

      if (!Connection.TCPPortIsAvailable(serverPort))
        throw new ArgumentException("port not available", "serverPort");

      p2pService = new P2PService(p2pServicePort, usingIPv6);
      systemTimer = new Timer(TimerCallback, null, SystemTimerInterval, -1);

      var address = usingIPv6 ? IPAddress.IPv6Any : IPAddress.Any;

      listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
      listener.Bind(new IPEndPoint(address, serverPort));
      listener.Listen(ListenConnections);
      listener.BeginAccept(AcceptCallback, null);

      isServerRunning = true;
    }

    public void RegisterConnection(string tempId, string id, RSAParameters openKey)
    {
      lock (connections)
      {
        var connection = GetConnection(tempId, true);
        if (connection == null)
          return;

        connections.Remove(tempId);
        connections.Add(id, connection);
        connection.Register(id, openKey);
      }
    }

    /// <summary>
    /// Закрывает соединение.
    /// </summary>
    /// <param name="id">Id cоединения, которое будет закрыто.</param>
    public void CloseConnection(string id)
    {
      P2PService.RemoveEndPoint(id);
      lock (connections)
      {
        var connection = GetConnection(id, true);
        if (connection == null)
          return;

        connections.Remove(id);
        connection.Dispose();
      }
    }

    /// <summary>
    /// Отсправляет сообщение по индетификатору соединения.
    /// </summary>
    /// <param name="connectionId">Id соединения.</param>
    /// <param name="messageId">Тип сообщения. (Command.Id)</param>
    /// <param name="messageContent">Контект команды.</param>
    /// <param name="allowTempConnections">Разрешить незарегестрированные соединения.</param>
    public void SendMessage(string connectionId, ushort messageId, object messageContent, bool allowTempConnections = false)
    {
      lock (connections)
      {
        var connection = GetConnection(connectionId, allowTempConnections);
        if (connection != null)
          connection.SendMessage(messageId, messageContent);
      }
    }

    /// <summary>
    /// Отсправляет сообщение по индетификатору соединения.
    /// </summary>
    /// <param name="connectionId">Id соединения.</param>
    /// <param name="messageId">Тип сообщения. (Command.Id)</param>
    /// <param name="messageContent">Сериализованный контект команды.</param>
    /// <param name="allowTempConnections">Разрешить незарегестрированные соединения.</param>
    public void SendMessage(string connectionId, ushort messageId, byte[] messageContent, bool allowTempConnections = false)
    {
      lock (connections)
      {
        var connection = GetConnection(connectionId, allowTempConnections);
        if (connection != null)
          connection.SendMessage(messageId, messageContent);
      }
    }

    /// <summary>
    /// Возвращает список зарегестрированых Id соединений.
    /// </summary>
    /// <returns>Список зарегестрированых Id.</returns>
    public string[] GetConnetionsIds()
    {
      lock (connections)
        return connections.Keys.Where(id => !id.Contains(Connection.TempConnectionPrefix)).ToArray();
    }

    /// <summary>
    /// Возвращает открытый ключ соединения.
    /// </summary>
    /// <param name="id">Идентификатор соединения.</param>
    /// <returns>Открытый ключ.</returns>
    public RSAParameters GetOpenKey(string id)
    {
      lock (connections)
      {
        var connection = GetConnection(id);
        return connection.OpenKey;
      }
    }

    /// <summary>
    /// Проверяет если на сервер соединение с таким Id.
    /// </summary>
    /// <param name="id">Id соединения.</param>
    /// <returns>Есть ли соединение.</returns>
    public bool ContainsConnection(string id)
    {
      lock (connections)
        return connections.ContainsKey(id);
    }
    #endregion

    #region private callback methods
    private void AcceptCallback(IAsyncResult result)
    {
      if (!isServerRunning)
        return;

      try
      {
        listener.BeginAccept(AcceptCallback, null);

        var handler = listener.EndAccept(result);
        var connection = new ServerConnection(handler, MaxDataSize, DataReceivedCallback);

        connection.SendAPIName(ServerModel.API.Name);

        lock (connections)
        {
          connection.Id = string.Format("{0}{1}", Connection.TempConnectionPrefix, lastTempId++);
          connections.Add(connection.Id, connection);
        }
      }
      catch (Exception e)
      {
        ServerModel.Logger.Write(e);
      }
    }

    private void DataReceivedCallback(object sender, DataReceivedEventArgs e)
    {
      try
      {
        if (e.Error != null)
          throw e.Error;

        if (!isServerRunning)
          return;

        var connectionId = ((ServerConnection)sender).Id;
        var command = ServerModel.API.GetCommand(e.ReceivedData);
        var args = new ServerCommandArgs
        {
          Message = e.ReceivedData,
          ConnectionId = connectionId,
        };

        requestQueue.Add(connectionId, command, args);
      }
      catch (Exception exc)
      {
        ServerModel.Logger.Write(exc);
      }
    }

    #region timer process
    private void TimerCallback(object arg)
    {
      RefreshConnections();
      RefreshRooms();

      lock (timerSync)
        if (systemTimer != null)
          systemTimer.Change(SystemTimerInterval, -1);
    }

    private void RefreshConnections()
    {
      List<string> removingUsers = null; // Prevent deadlock (in RemoveUser locked ServerModel)

      lock (connections)
      {
        string[] keys = connections.Keys.ToArray();
        foreach (string id in keys)
        {
          try
          {
            if (connections[id].UnregisteredTimeInterval >= ServerConnection.UnregisteredTimeOut)
            {
              CloseConnection(id);
              continue;
            }

#if !DEBUG
            if (connections[id].IntervalOfSilence >= ServerConnection.ConnectionTimeOut)
            {
              (removingUsers ?? (removingUsers = new List<string>())).Add(id);
              continue;
            }
#endif
          }
          catch (Exception e)
          {
            ServerModel.Logger.Write(e);
          }
        }
      }

      if (removingUsers != null)
        foreach (var id in removingUsers)
        {
          try
          {
            ServerModel.API.RemoveUser(id);
          }
          catch(Exception e)
          {
            ServerModel.Logger.Write(e);
            CloseConnection(id);
          }
        }
    }

    private void RefreshRooms()
    {
      if (ServerModel.IsInited)
        using (var server = ServerModel.Get())
        {
          string[] roomsNames = server.Rooms.Keys.ToArray();
          foreach (string name in roomsNames)
          {
            if (string.Equals(name, ServerModel.MainRoomName))
              continue;

            if (server.Rooms[name].Count == 0)
              server.Rooms.Remove(name);
          }
        }
    }
    #endregion
    #endregion

    #region private methods
    private ServerConnection GetConnection(string connectionId, bool allowTempConnections = false)
    {
      if (connectionId.Contains(Connection.TempConnectionPrefix) && !allowTempConnections)
        throw new InvalidOperationException("this connection don't registered");

      ServerConnection connection;
      if (!connections.TryGetValue(connectionId, out connection))
      {
        ServerModel.Logger.WriteWarning("Connection {0} don't finded", connectionId);
        return null;
      }

      return connection;
    }
    #endregion

    #region IDisposable
    bool disposed = false;

    private void ThrowIfDisposed()
    {
      if (disposed)
        throw new ObjectDisposedException("Object disposed");
    }

    private void ReleaseManagedResource()
    {
      if (disposed)
        return;

      isServerRunning = false;

      if (requestQueue != null)
        requestQueue.Dispose();

      lock (timerSync)
      {
        if (systemTimer != null)
          systemTimer.Dispose();

        systemTimer = null;
      }

      lock (connections)
      {
        foreach (var connection in connections.Values)
          connection.Dispose();

        connections.Clear();
      }

      if (listener != null)
        listener.Close();

      if (p2pService != null)
        p2pService.Dispose();

      disposed = true;
    }

    /// <summary>
    /// Особождает все ресуры используемые сервером.
    /// </summary>
    public void Dispose()
    {
      ReleaseManagedResource();
    }
    #endregion
  }
}
