// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ReverseProxy.Service.Management;
using Microsoft.ReverseProxy.Service.Proxy.Infrastructure;
using Microsoft.ReverseProxy.Signals;
using Microsoft.ReverseProxy.Utilities;

namespace Microsoft.ReverseProxy.RuntimeModel
{
    /// <summary>
    /// Representation of a cluster for use at runtime.
    /// </summary>
    /// <remarks>
    /// Note that while this class is immutable, specific members such as
    /// <see cref="Config"/> and <see cref="DynamicState"/> hold mutable references
    /// that can be updated atomically and which will always have latest information
    /// relevant to this cluster.
    /// All members are thread safe.
    /// </remarks>
    internal sealed class ClusterInfo
    {
        public ClusterInfo(string clusterId, IDestinationManager destinationManager, IProxyHttpClientFactory proxyHttpClientFactory)
        {
            ClusterId = clusterId ?? throw new ArgumentNullException(nameof(clusterId));
            DestinationManager = destinationManager ?? throw new ArgumentNullException(nameof(destinationManager));
            ProxyHttpClientFactory = proxyHttpClientFactory ?? throw new ArgumentNullException(nameof(proxyHttpClientFactory));

            DynamicState = CreateDynamicStateQuery();
        }

        public string ClusterId { get; }

        public IDestinationManager DestinationManager { get; }

        /// <summary>
        /// Used to create instances of <see cref="System.Net.Http.HttpClient"/>
        /// when proxying requests to this cluster.
        /// </summary>
        public IProxyHttpClientFactory ProxyHttpClientFactory { get; }

        /// <summary>
        /// Encapsulates parts of a cluster that can change atomically
        /// in reaction to config changes.
        /// </summary>
        public Signal<ClusterConfig> Config { get; } = SignalFactory.Default.CreateSignal<ClusterConfig>();

        /// <summary>
        /// Encapsulates parts of a cluster that can change atomically
        /// in reaction to runtime state changes (e.g. dynamic endpoint discovery).
        /// </summary>
        public IReadableSignal<ClusterDynamicState> DynamicState { get; }

        /// <summary>
        /// Keeps track of the total number of concurrent requests on this cluster.
        /// </summary>
        public AtomicCounter ConcurrencyCounter { get; } = new AtomicCounter();

        /// <summary>
        /// Sets up the data flow that keeps <see cref="DynamicState"/> up to date.
        /// See <c>Signals\Readme.md</c> for more information.
        /// </summary>
        private IReadableSignal<ClusterDynamicState> CreateDynamicStateQuery()
        {
            var endpointsAndStateChanges =
                DestinationManager.Items
                    .SelectMany(endpoints =>
                        endpoints
                            .Select(endpoint => endpoint.DynamicState)
                            .AnyChange())
                    .DropValue();

            return new[] { endpointsAndStateChanges, Config.DropValue() }
                .AnyChange() // If any of them change...
                .Select(
                    _ =>
                    {
                        var allDestinations = DestinationManager.Items.Value ?? new List<DestinationInfo>().AsReadOnly();
                        var healthyEndpoints = (Config.Value?.HealthCheckOptions.Enabled ?? false)
                            ? allDestinations.Where(endpoint => endpoint.DynamicState.Value?.Health == DestinationHealth.Healthy).ToList().AsReadOnly()
                            : allDestinations;
                        return new ClusterDynamicState(
                            allDestinations: allDestinations,
                            healthyDestinations: healthyEndpoints);
                    });
        }
    }
}
