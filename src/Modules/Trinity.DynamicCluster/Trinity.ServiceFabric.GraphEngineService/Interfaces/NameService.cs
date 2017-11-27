﻿using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Runtime;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Trinity.Daemon;
using Trinity.Diagnostics;
using Trinity.DynamicCluster;
using Trinity.DynamicCluster.Consensus;
using Trinity.Network;

namespace Trinity.ServiceFabric
{
    public class NameService : INameService
    {
        private const int c_bgtaskInterval = 10000;
        private Task m_bgtask;
        private CancellationToken m_token;
        private Dictionary<Guid, HashSet<string>> m_replicaList;
        private List<Guid> m_partitionIds;
        private FabricClient m_fclient;
        private Uri m_svcuri;

        public string Address => GraphEngineService.Instance.Address;

        public int Port => GraphEngineService.Instance.Port;

        public int HttpPort => GraphEngineService.Instance.HttpPort;

        public Guid InstanceId { get; private set; }

        public bool IsMaster => GraphEngineService.Instance.Role == ReplicaRole.Primary;


        public NameService()
        {
            InstanceId = GetInstanceId();
            m_partitionIds = GraphEngineService.Instance.Partitions.Select(_ => _.PartitionInformation.Id).ToList();
            m_replicaList = Enumerable.ToDictionary(m_partitionIds, _ => _, _ => new HashSet<string>());
            m_svcuri = GraphEngineService.Instance.Context.ServiceName;
            m_fclient = new FabricClient();
        }

        internal static Guid GetInstanceId()
        {
            BigInteger low = new BigInteger(GraphEngineService.Instance.Context.ReplicaId);
            BigInteger high = new BigInteger(GraphEngineService.Instance.PartitionId) << 64;
            return new Guid(Enumerable.Concat((low + high).ToByteArray(),
                                        Enumerable.Repeat<byte>(0x0, 16))
                                       .Take(16).ToArray());
        }

        //internal static Guid GetInstanceId() => new Guid(Enumerable.Concat(
        //                                    new BigInteger(GraphEngineService.Instance.Context.ReplicaOrInstanceId).ToByteArray(),
        //                                    Enumerable.Repeat<byte>(0x0, 16))
        //                                   .Take(16).ToArray());

        public TrinityErrorCode Start(CancellationToken token)
        {
            m_token = token;
            ServerInfo my_si = new ServerInfo(Address, Port, Global.MyAssemblyPath, TrinityConfig.LoggingLevel);
            m_bgtask = ScanNodesProc();

            return TrinityErrorCode.E_SUCCESS;
        }

        public void Dispose()
        {
            m_bgtask.Wait();
        }

        private async Task ScanNodesProc()
        {
            while (true)
            {
                if (m_token.IsCancellationRequested) return;
                try
                {
                    var tasks = m_partitionIds.Select(ResolvePartition);
                    await Task.WhenAll(tasks);
                    m_partitionIds.Zip(tasks.Select(_ => _.Result), UpdatePartition).ToList();
                }
                catch (Exception ex)
                {
                    Log.WriteLine(LogLevel.Error, $"ScanNodesProc: {ex.ToString()}");
                }

                await Task.Delay(c_bgtaskInterval);
            }
        }

        private int UpdatePartition(Guid id, HashSet<string> newset)
        {
            var oldset = m_replicaList[id];
            var tmp = new HashSet<string>(newset);
            newset.ExceptWith(oldset);
            foreach (var addr in newset)
            {
                var ents = addr.Substring("tcp://".Length).Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if ($"{ents[0]}:{ents[1]}" == $"{this.Address}:{this.Port}") continue;
                Log.WriteLine("{0}", $"NameService: {addr} added to partition {m_partitionIds.FindIndex(_ => _ == id)} ({id})");
                NewServerInfoPublished(this, new ServerInfo(ents[0], int.Parse(ents[1]), null, LogLevel.Info));
            }
            m_replicaList[id] = tmp;
            return 0;
        }

        private async Task<HashSet<string>> ResolvePartition(Guid partId)
        {
            var rs = await m_fclient.QueryManager.GetReplicaListAsync(partId);
            //rs.ForEach(r => Log.WriteLine("{0}", r.ReplicaAddress));
            var addrs = rs.Select(r => GetTrinityProtocolEndpoint(r)).Where(_ => _ != null);
            return new HashSet<string>(addrs);
        }

        private static string GetTrinityProtocolEndpoint(System.Fabric.Query.Replica r)
        {
            try { return JObject.Parse(r.ReplicaAddress)["Endpoints"]["GraphEngineTrinityProtocolListener"].ToString(); }
            catch { return null; }
        }

        public event EventHandler<ServerInfo> NewServerInfoPublished = delegate { };
    }
}
