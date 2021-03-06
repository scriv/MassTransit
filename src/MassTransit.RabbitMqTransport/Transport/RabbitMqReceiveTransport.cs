﻿// Copyright 2007-2016 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.RabbitMqTransport.Transport
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Events;
    using GreenPipes;
    using GreenPipes.Internals.Extensions;
    using Logging;
    using MassTransit.Pipeline.Observables;
    using MassTransit.Pipeline.Pipes;
    using Policies;
    using Topology;
    using Transports;
    using Util;


    public class RabbitMqReceiveTransport :
        IReceiveTransport
    {
        static readonly ILog _log = Logger.Get<RabbitMqReceiveTransport>();
        readonly ExchangeBindingSettings[] _bindings;
        readonly IRabbitMqHost _host;
        readonly Uri _inputAddress;
        readonly IManagementPipe _managementPipe;
        readonly IPublishEndpointProvider _publishEndpointProvider;
        readonly ReceiveObservable _receiveObservable;
        readonly ReceiveTransportObservable _receiveTransportObservable;
        readonly ISendEndpointProvider _sendEndpointProvider;
        readonly ReceiveSettings _settings;

        public RabbitMqReceiveTransport(IRabbitMqHost host, ReceiveSettings settings, IManagementPipe managementPipe, ExchangeBindingSettings[] bindings,
            ISendEndpointProvider sendEndpointProvider, IPublishEndpointProvider publishEndpointProvider)
        {
            _host = host;
            _settings = settings;
            _bindings = bindings;
            _sendEndpointProvider = sendEndpointProvider;
            _publishEndpointProvider = publishEndpointProvider;
            _managementPipe = managementPipe;

            _receiveObservable = new ReceiveObservable();
            _receiveTransportObservable = new ReceiveTransportObservable();

            _inputAddress = _settings.GetInputAddress(_host.Settings.HostAddress);
        }

        void IProbeSite.Probe(ProbeContext context)
        {
            ProbeContext scope = context.CreateScope("transport");
            scope.Add("type", "RabbitMQ");
            scope.Set(_settings);
            scope.Add("bindings", _bindings);
        }

        /// <summary>
        /// Start the receive transport, returning a Task that can be awaited to signal the transport has 
        /// completely shutdown once the cancellation token is cancelled.
        /// </summary>
        /// <param name="receivePipe"></param>
        /// <returns>A task that is completed once the transport is shut down</returns>
        public ReceiveTransportHandle Start(IPipe<ReceiveContext> receivePipe)
        {
            var supervisor = new TaskSupervisor($"{TypeCache<RabbitMqReceiveTransport>.ShortName} - {_inputAddress}");

            IPipe<ConnectionContext> pipe = Pipe.New<ConnectionContext>(x =>
            {
                x.RabbitMqConsumer(receivePipe, _settings, _receiveObservable, _receiveTransportObservable, _bindings, supervisor, _managementPipe,
                    _sendEndpointProvider, _publishEndpointProvider, _host);
            });

            Receiver(pipe, supervisor);

            return new Handle(supervisor);
        }

        public ConnectHandle ConnectReceiveObserver(IReceiveObserver observer)
        {
            return _receiveObservable.Connect(observer);
        }

        public ConnectHandle ConnectReceiveTransportObserver(IReceiveTransportObserver observer)
        {
            return _receiveTransportObservable.Connect(observer);
        }

        public ConnectHandle ConnectPublishObserver(IPublishObserver observer)
        {
            return _publishEndpointProvider.ConnectPublishObserver(observer);
        }

        async void Receiver(IPipe<ConnectionContext> transportPipe, TaskSupervisor supervisor)
        {
            try
            {
                await _host.ConnectionRetryPolicy.RetryUntilCancelled(async () =>
                {
                    try
                    {
                        await _host.ConnectionCache.Send(transportPipe, supervisor.StoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (RabbitMqConnectionException ex)
                    {
                        if (_log.IsErrorEnabled)
                            _log.ErrorFormat("RabbitMQ connection failed: {0}", ex.Message);

                        await _receiveTransportObservable.Faulted(new ReceiveTransportFaultedEvent(_inputAddress, ex)).ConfigureAwait(false);

                        throw;
                    }
                    catch (TaskCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (_log.IsErrorEnabled)
                            _log.ErrorFormat("RabbitMQ receive transport failed: {0}", ex.Message);

                        await _receiveTransportObservable.Faulted(new ReceiveTransportFaultedEvent(_inputAddress, ex)).ConfigureAwait(false);

                        throw;
                    }
                }, supervisor.StoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
            }
        }


        class Handle :
            ReceiveTransportHandle
        {
            readonly TaskSupervisor _supervisor;

            public Handle(TaskSupervisor supervisor)
            {
                _supervisor = supervisor;
            }

            Task ReceiveTransportHandle.Stop(CancellationToken cancellationToken)
            {
                return _supervisor.Stop("Stop Receive Transport", cancellationToken);
            }
        }
    }
}