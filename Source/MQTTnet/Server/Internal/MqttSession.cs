﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Client;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server
{
    public sealed class MqttSession : IDisposable
    {
        readonly MqttPacketBus _packetBus = new MqttPacketBus();

        readonly Dictionary<ushort, MqttPublishPacket> _unacknowledgedPublishPackets = new Dictionary<ushort, MqttPublishPacket>();

        readonly MqttServerOptions _serverOptions;
        readonly MqttClientSessionsManager _clientSessionsManager;

        public MqttSession(string clientId,
            bool isPersistent,
            IDictionary<object, object> items,
            MqttServerOptions serverOptions,
            MqttServerEventContainer eventContainer,
            MqttRetainedMessagesManager retainedMessagesManager,
            MqttClientSessionsManager clientSessionsManager)
        {
            Id = clientId ?? throw new ArgumentNullException(nameof(clientId));
            IsPersistent = isPersistent;
            Items = items ?? throw new ArgumentNullException(nameof(items));
            
            _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
            _clientSessionsManager = clientSessionsManager ?? throw new ArgumentNullException(nameof(clientSessionsManager));
            
            SubscriptionsManager = new MqttClientSubscriptionsManager(this, serverOptions, eventContainer, retainedMessagesManager);
        }

        public event EventHandler Deleted;

        public string Id { get; }
        
        /// <summary>
        /// Session should persist if CleanSession was set to false (Mqtt3) or if SessionExpiryInterval != 0 (Mqtt5)
        /// </summary>
        public bool IsPersistent { get; }

        public MqttPacketIdentifierProvider PacketIdentifierProvider { get; } = new MqttPacketIdentifierProvider();

        public DateTime CreatedTimestamp { get; } = DateTime.UtcNow;

        public MqttConnectPacket LatestConnectPacket { get; set; }

        public MqttClientSubscriptionsManager SubscriptionsManager { get; }

        public IDictionary<object, object> Items { get; }

        public bool WillMessageSent { get; set; }

        public long PendingDataPacketsCount => _packetBus.DataPacketsCount;

        public void AcknowledgePublishPacket(ushort packetIdentifier)
        {
            _unacknowledgedPublishPackets.Remove(packetIdentifier);
        }

        public void EnqueuePacket(MqttPacketBusItem packetBusItem)
        {
            if (packetBusItem == null) throw new ArgumentNullException(nameof(packetBusItem));

            if (_packetBus.PacketsCount >= _serverOptions.MaxPendingMessagesPerClient)
            {
                if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropNewMessage)
                {
                    return;
                }
                else
                {
                    // TODO: Implement.
                }
            }

            if (packetBusItem.Packet is MqttPublishPacket publishPacket)
            {
                if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                {
                    _unacknowledgedPublishPackets[publishPacket.PacketIdentifier] = publishPacket;
                }

                _packetBus.Enqueue(packetBusItem, MqttPacketBusPartition.Data);
            }
            else if (packetBusItem.Packet is MqttPingReqPacket || packetBusItem.Packet is MqttPingRespPacket)
            {
                _packetBus.Enqueue(packetBusItem, MqttPacketBusPartition.Health);
            }
            else
            {
                _packetBus.Enqueue(packetBusItem, MqttPacketBusPartition.Control);
            }
        }

        public Task<MqttPacketBusItem> DequeuePacketAsync(CancellationToken cancellationToken)
        {
            return _packetBus.DequeueAsync(cancellationToken);
        }

        public Task DeleteAsync()
        {
            return _clientSessionsManager.DeleteSessionAsync(Id);
        }

        public void OnDeleted()
        {
            Deleted?.Invoke(this, EventArgs.Empty);
        }

        public void Recover()
        {
            // TODO: Keep the bus and only insert pending items again.
            // TODO: Check if packet identifier must be restarted or not.
            _packetBus.Clear();

            foreach (var publishPacket in _unacknowledgedPublishPackets.Values.ToList())
            {
                EnqueuePacket(new MqttPacketBusItem(publishPacket));
            }
        }

        public void Dispose()
        {
            _packetBus?.Dispose();
        }
    }
}