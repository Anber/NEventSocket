﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ObservableListener.cs" company="Dan Barua">
//   (C) Dan Barua and contributors. Licensed under the Mozilla Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------
namespace NEventSocket.Sockets
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Reactive;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;

    using NEventSocket.Logging;
    using NEventSocket.Util;

    /// <summary>
    /// A Reactive wrapper around a TcpListener
    /// </summary>
    /// <typeparam name="T">The type of <seealso cref="ObservableSocket"/> that this listener will provide.</typeparam>
    public abstract class ObservableListener<T> : IDisposable where T : ObservableSocket
    {
        private readonly ILog Log;

        private readonly Subject<Unit> listenerTermination = new Subject<Unit>();

        private readonly List<T> connections = new List<T>();

        private readonly Subject<T> observable = new Subject<T>();

        private readonly CompositeDisposable disposables = new CompositeDisposable();

        private readonly int port;

        private readonly Func<TcpClient, T> observableSocketFactory;

        private bool disposed;

        private IDisposable subscription;

        private TcpListener tcpListener;

        /// <summary>
        /// Starts the Listener on the given port
        /// </summary>
        /// <param name="port">The Tcp Port on which to listen for incoming connections.</param>
        /// <param name="observableSocketFactory">A function returning an object that inherits from <seealso cref="ObservableSocket" />.</param>
        protected ObservableListener(int port, Func<TcpClient, T> observableSocketFactory)
        {
            Log = LogProvider.GetLogger(GetType());
            this.port = port;
            this.observableSocketFactory = observableSocketFactory;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="ObservableListener{T}"/> class.
        /// </summary>
        ~ObservableListener()
        {
            Dispose(false);
        }

        /// <summary>
        /// Gets an observable sequence of all outbound connections from FreeSwitch.
        /// </summary>
        public IObservable<T> Connections
        {
            get
            {
                return observable;
            }
        }

        /// <summary>
        /// Gets the Tcp Port that the Listener is waiting for connections on.
        /// </summary>
        public int Port
        {
            get
            {
                return ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            }
        }

        /// <summary>
        /// Starts the Listener
        /// </summary>
        public void Start()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(ToString());
            }

            tcpListener = new TcpListener(IPAddress.Any, port);

            tcpListener.Start();

            Log.Trace(() => "Listener Started on Port {0}".Fmt(Port));

            subscription =
                Observable.FromAsync(tcpListener.AcceptTcpClientAsync)
                          .Repeat()
                          .TakeUntil(listenerTermination)
                          .Do(connection => Log.Trace(() => "New Connection from {0}".Fmt(connection.Client.RemoteEndPoint)))
                          .Select(tcpClient => observableSocketFactory(tcpClient))
                          .Subscribe(
                              connection =>
                                  {
                                      connections.Add(connection);
                                      observable.OnNext(connection);

                                      disposables.Add(
                                          Observable.FromEventPattern(h => connection.Disposed += h, h => connection.Disposed -= h)
                                                    .FirstAsync()
                                                    .Subscribe(_ =>
                                                        {
                                                            Log.Trace(() => "Connection Disposed");
                                                            connections.Remove(connection);
                                                        }));
                                  }, 
                              ex => Log.ErrorFormat("Error handling inbound connection", ex));
        }

        /// <summary>
        /// Stops and closes down the Listener.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                Log.Trace(() => "Disposing (disposing:{0})".Fmt(disposing));

                if (disposing)
                {
                    listenerTermination.OnNext(Unit.Default);
                    listenerTermination.Dispose();

                    if (subscription != null)
                    {
                        subscription.Dispose();
                        subscription = null;
                    }

                    disposables.Dispose();
                    connections.ToList().ForEach(connection => connection.Dispose());

                    observable.OnCompleted();
                    observable.Dispose();

                    if (tcpListener != null)
                    {
                        tcpListener.Stop();
                        tcpListener = null;
                    }
                }

                Log.Trace(() => "Disposed");
                disposed = true;
            }
        }
    }
}