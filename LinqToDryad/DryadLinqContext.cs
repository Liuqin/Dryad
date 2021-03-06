/*
Copyright (c) Microsoft Corporation

All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in 
compliance with the License.  You may obtain a copy of the License 
at http://www.apache.org/licenses/LICENSE-2.0   


THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER 
EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF 
TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.  


See the Apache Version 2.0 License for specific language governing permissions and 
limitations under the License. 

*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Research.DryadLinq.Internal;
using Microsoft.Research.Peloponnese.ClusterUtils;
using Microsoft.Research.Peloponnese.Shared;
using Microsoft.Research.Peloponnese.Hdfs;
using Microsoft.Research.Peloponnese.WebHdfs;
using Microsoft.Research.Peloponnese.Azure;
using Microsoft.Research.Peloponnese.Yarn;

namespace Microsoft.Research.DryadLinq
{
    /// <summary>
    /// The executor to run DryadLINQ jobs. The current release only supports Dryad.
    /// </summary>
    public enum ExecutorKind
    {
        /// <summary>
        /// Run DryadLINQ using Dryad.
        /// </summary>
        DRYAD
    }

    /// <summary>
    /// The service platforms where you can run DryadLINQ.
    /// </summary>
    public enum PlatformKind
    {
        /// <summary>
        /// run directly on a YARN cluster
        /// </summary>
        YARN_NATIVE,

        /// <summary>
        /// run on a YARN cluster in Azure HDInsight
        /// </summary>
        YARN_AZURE,

        /// <summary>
        /// run locally at client side
        /// </summary>
        LOCAL
    }

    /// <summary>
    /// Base interface for cluster types that the DryadLinqContext constructor can accept.
    /// </summary>
    public interface DryadLinqCluster
    {
        /// <summary>
        /// Gets the service platform of this cluster.
        /// </summary>
        PlatformKind Kind { get; }
        /// <summary>
        /// Gets the hostname of the head node of the cluster.
        /// </summary>
        string HeadNode { get; }
        /// <summary>
        /// Gets the client DFS interface.
        /// </summary>
        DfsClient DfsClient { get; }
        /// <summary>
        /// Gets the client cluster interface.
        /// </summary>
        /// <param name="context">An instnace of DryadLinqContext</param>
        /// <returns>The client interface to the cluster</returns>
        ClusterClient Client(DryadLinqContext context);
        /// <summary>
        /// Makes a new URI for storing a dataset in the DFS.
        /// </summary>
        /// <param name="path">A user provided local path</param>
        /// <returns>A new URI that can be used to store a dataset</returns>
        Uri MakeInternalClusterUri(params string[] path);
        /// <summary>
        /// The maximum number of nodes to use for a job if JobMaxNodes is not set in DryadLinqContext
        /// </summary>
        int DefaultMaxNodes { get; }
    }

    /// <summary>
    /// The interface for a YARN native cluster.
    /// </summary>
    public class DryadLinqYarnCluster : DryadLinqCluster
    {
        /// <summary>
        /// The username for the job on the YARN cluster
        /// </summary>
        public string User;
        /// <summary>
        /// The hostname of the computer where the YARN Resource Node is running
        /// </summary>
        public string HeadNode { get; set; }
        /// <summary>
        /// The port that the YARN Resource Node is listening on
        /// </summary>
        public int YarnPort;
        /// <summary>
        /// The queue that should be used on the cluster
        /// </summary>
        public string Queue { get; set; }
        /// <summary>
        /// The hostname of the computer where the HDFS instance for resource staging is running
        /// </summary>
        public string NameNode;
        /// <summary>
        /// The port that the Hdfs protocol is listening on
        /// </summary>
        public int HdfsPort;
        /// <summary>
        /// The port that the WebHdfs protocol is listening on, or -1 if Java/YARN are present on the client
        /// </summary>
        public int WebHdfsPort;
        /// <summary>
        /// The hostname of the computer where the DryadLINQ YARN launcher is running, or null if Java/YARN are
        /// present on the client
        /// </summary>
        public string LauncherNode;
        /// <summary>
        /// The port where the DryadLINQ Yarn launcher is listening, or -1 if Java/YARN are
        /// present on the client
        /// </summary>
        public int LauncherPort;
        /// <summary>
        /// The number of containers a job should ask for, if JobMaxNodes and JobMinNodes are not overwritten in
        /// the DryadLinqContext
        /// </summary>
        public int DefaultNumberOfContainers;
        /// <summary>
        /// The amount of memory to be requested for the Application Master container
        /// </summary>
        public int ApplicationMasterMbMemory;
        /// <summary>
        /// The amount of memory to be requested for each container other than the Application Master
        /// </summary>
        public int ContainerMbMemory;


        private DfsClient _dfsClient;
        private NativeYarnClient _clusterClient;

        /// <summary>
        /// Make a new cluster object representing a YARN cluster with default ports for a client with Java installed
        /// </summary>
        /// <param name="user">username for the cluster job</param>
        /// <param name="defaultNumberOfContainers">Number of containers the job will ask for, if JobMaxNodes and
        /// JobMinNodes are not overwritten in the DryadLinqContext</param>
        ///  /// <param name="appMasterMbMemory">The amount of memory, in Megabytes, that should be 
        /// requested for the Application Master container</param> 
        /// <param name="containerMbMemory">The amount of memory, in Megabytes, that should be 
        /// requested for containers other than the Application Master</param>
        /// <param name="queue">The queue to submit the job to, or null for "default"</param>
        /// <param name="resourceNode">computer where the YARN resource node is running</param>
        /// <param name="yarnPort">port the YARN resource node is listening on</param>
        /// <param name="nameNode">HDFS namenode to use for resource staging</param>
        /// <param name="hdfsPort">HDFS namenode port, or -1 for filesystem default</param>
        public DryadLinqYarnCluster(
            string user,
            int defaultNumberOfContainers,
            int appMasterMbMemory,
            int containerMbMemory,
            string queue,
            string resourceNode, int yarnPort,
            string nameNode, int hdfsPort)
        {
            User = user;
            DefaultNumberOfContainers = defaultNumberOfContainers;
            ApplicationMasterMbMemory = appMasterMbMemory;
            ContainerMbMemory = containerMbMemory;
            if (queue == null)
            {
                Queue = "default";
            }
            else 
            {
                Queue = queue; 
            }

            HeadNode = resourceNode;
            YarnPort = yarnPort;

            NameNode = nameNode;
            HdfsPort = hdfsPort;

            WebHdfsPort = -1;

            LauncherNode = null;
            LauncherPort = -1;

            _dfsClient = null;
            _clusterClient = null;
        }

        /// <summary>
        /// Make a new cluster object representing a YARN cluster with default ports for a client without Java installed
        /// </summary>
        /// <param name="user">username for the cluster job</param>
        /// <param name="defaultNumberOfContainers">Number of containers the job will ask for, if JobMaxNodes and
        /// JobMinNodes are not overwritten in the DryadLinqContext</param>
        /// <param name="appMasterMbMemory">The amount of memory, in Megabytes, that should be 
        /// requested for the Application Master container</param> 
        /// <param name="containerMbMemory">The amount of memory, in Megabytes, that should be 
        /// requested for containers other than the Application Master</param>
        /// <param name="queue">The queue to submit the job to, or null for "default"</param>
        /// <param name="resourceNode">computer where the YARN resource node is running</param>
        /// <param name="yarnPort">port the YARN resource node is listening on</param>
        /// <param name="nameNode">HDFS namenode to use for resource staging</param>
        /// <param name="hdfsPort">HDFS namenode port, or -1 for filesystem default</param>
        /// <param name="webHdfsPort">HDFS WebHdfs port</param>
        /// <param name="launcherNode">computer where DryadLINQ Yarn launcher is running</param>
        /// <param name="launcherPort">port that DryadLINQ Yarn launcher is listening on</param>
        public DryadLinqYarnCluster(
            string user,
            int defaultNumberOfContainers,
            int appMasterMbMemory,
            int containerMbMemory,
            string queue,
            string resourceNode, int yarnPort,
            string nameNode, int hdfsPort, int webHdfsPort,
            string launcherNode, int launcherPort)
        {
            User = user;
            DefaultNumberOfContainers = defaultNumberOfContainers;
            ApplicationMasterMbMemory = appMasterMbMemory;
            ContainerMbMemory = containerMbMemory;
            if (queue == null)
            {
                Queue = "default";
            }
            else 
            {
                Queue = queue; 
            }

            HeadNode = resourceNode;
            YarnPort = yarnPort;

            NameNode = nameNode;
            HdfsPort = hdfsPort;

            WebHdfsPort = webHdfsPort;

            LauncherNode = launcherNode;
            LauncherPort = launcherPort;

            _dfsClient = null;
            _clusterClient = null;
        }

        /// <summary>
        /// Gets the service platform of this cluster.
        /// </summary>
        public PlatformKind Kind 
        {
            get { return PlatformKind.YARN_NATIVE; } 
        }

        /// <summary>
        /// Gets the client DFS interface.
        /// </summary>
        public DfsClient DfsClient
        {
            get
            {
                if (_dfsClient == null)
                {
                    if (WebHdfsPort > 0)
                    {
                        _dfsClient = new WebHdfsClient(User, WebHdfsPort);
                    }
                    else
                    {
                        _dfsClient = new HdfsClient(User);
                    }
                }
                return _dfsClient;
            }
        }

        /// <summary>
        /// Gets the client cluster interface.
        /// </summary>
        /// <param name="context">An instnace of DryadLinqContext</param>
        /// <returns>The client interface to the cluster</returns>
        public ClusterClient Client(DryadLinqContext context)
        {
            if (_clusterClient == null)
            {
                if (LauncherNode == null)
                {
                    string yarnHomeDirectory = Environment.GetEnvironmentVariable("HADOOP_COMMON_HOME");
                    if (yarnHomeDirectory == null)
                    {
                        throw new ApplicationException("Yarn client needs HADOOP_COMMON_HOME environment variable set to YARN installation");
                    }
                    _clusterClient = new NativeYarnClient(
                        HeadNode, YarnPort,
                        DfsClient, MakeInternalClusterUri("user", User),
                        "Microsoft.Research.Peloponnese.YarnLauncher.jar",
                        yarnHomeDirectory);
                }
                else
                {
                    _clusterClient = new NativeYarnClient(
                        HeadNode, YarnPort,
                        DfsClient, MakeInternalClusterUri("user", User),
                        LauncherNode, LauncherPort);
                }
            }
            return _clusterClient;
        }

        /// <summary>
        /// Makes a new URI for storing a dataset in the DFS.
        /// </summary>
        /// <param name="path">A user provided local path</param>
        /// <returns>A new URI that can be used to store a dataset</returns>
        public Uri MakeInternalClusterUri(params string[] path)
        {
            UriBuilder builder = new UriBuilder();
            builder.Scheme = "hdfs";
            builder.Host = NameNode;
            builder.Port = HdfsPort;
            return DfsClient.Combine(builder.Uri, path);
        }

        /// <summary>
        /// The maximum number of nodes to use for a job if JobMinNodes is not set in DryadLinqContext
        /// </summary>
        public int DefaultMaxNodes { get { return DefaultNumberOfContainers; } }
    }

    /// <summary>
    /// The interface for a YARN Azure cluster.
    /// </summary>
    internal class DryadLinqAzureCluster : DryadLinqCluster
    {
        /// <summary>
        /// The name of the HDInsight cluster
        /// </summary>
        public string HeadNode { get
        { 
            AzureCluster cluster  =_cluster.Result;
            if (cluster == null)
            {
                throw new ApplicationException("Cluster is not known: are your Powershell Azure defaults set?");
            }
            return cluster.Name;
        } }

        private readonly AzureSubscriptions _azureSubscriptions;
        private readonly Task<AzureCluster> _cluster;
        private readonly Task<AzureDfsClient> _dfsClient;
        private Task<AzureYarnClient> _clusterClient;

        /// <summary>
        /// Make a new cluster object representing an Azure HDInsight cluster, reading the details
        /// from a subscription stored in the Powershell defaults.
        /// </summary>
        /// <param name="clusterName">The name of the HDInsight cluster</param>
        public DryadLinqAzureCluster(string clusterName)
        {
            // start fetching details about the subscriptions, available clusters, etc.
            _azureSubscriptions = new AzureSubscriptions();
            _cluster = _azureSubscriptions.GetClusterAsync(clusterName);
            _dfsClient = _cluster.ContinueWith(
                c =>
                {
                    if (c.Result == null)
                    {
                        throw new ApplicationException("Cluster " + clusterName + " is not known: are your Powershell Azure defaults set?");
                    }
                    return new AzureDfsClient(c.Result.StorageAccount, c.Result.StorageKey, "staging");
                });
        }

        /// <summary>
        /// Make a new cluster object representing an Azure HDInsight cluster, specifying the details
        /// manually.
        /// </summary>
        /// <param name="clusterName">The name of the HDInsight cluster</param>
        /// <param name="storageAccount">The storage account to use for staging job resources</param>
        /// <param name="storageContainer">The storage account container to use for staging job resources</param>
        /// <param name="storageKey">The storage account key, which will be looked up in the subscription if null</param>
        public DryadLinqAzureCluster(string clusterName, string storageAccount, string storageContainer, string storageKey = null)
        {
            // start fetching details about the subscriptions, available clusters, etc.
            _azureSubscriptions = new AzureSubscriptions();
            if (storageKey != null)
            {
                _azureSubscriptions.AddAccount(storageAccount, storageKey);
            }
            _cluster = _azureSubscriptions.GetClusterAsync(clusterName)
                                          .ContinueWith(c =>
                                          {
                                              if (c.Result == null)
                                              {
                                                  throw new ApplicationException("Cluster " + clusterName + " is not known: are your Powershell Azure defaults set?");
                                              }
                                              c.Result.SetStorageAccount(storageAccount, storageKey);
                                              return c.Result;
                                          });
            _dfsClient = _cluster.ContinueWith(
                c => new AzureDfsClient(c.Result.StorageAccount, c.Result.StorageKey, storageContainer));
        }

        /// <summary>
        /// Make a new cluster object representing an Azure HDInsight cluster, specifying the details
        /// manually.
        /// </summary>
        /// <param name="clusterName">The name of the HDInsight cluster</param>
        /// <param name="subscriptionId">The ID of the subscription to fetch cluster details from</param>
        /// <param name="certificateThumbprint">The thumbprint of the certificate associated with the subscription</param>
        public DryadLinqAzureCluster(string clusterName, string subscriptionId, string certificateThumbprint)
        {
            // start fetching details about the subscriptions, available clusters, etc.
            _azureSubscriptions = new AzureSubscriptions();
            _azureSubscriptions.AddSubscription(subscriptionId, certificateThumbprint);
            _cluster = _azureSubscriptions.GetClusterAsync(clusterName);
            _dfsClient = _cluster.ContinueWith(c => new AzureDfsClient(c.Result.StorageAccount, c.Result.StorageKey, "staging"));
        }

        /// <summary>
        /// Make a new cluster object representing an Azure HDInsight cluster, specifying the details
        /// manually.
        /// </summary>
        /// <param name="clusterName">The name of the HDInsight cluster</param>
        /// <param name="subscriptionId">The ID of the subscription to fetch cluster details from</param>
        /// <param name="certificate">The certificate associated with the subscription</param>
        public DryadLinqAzureCluster(string clusterName, string subscriptionId, X509Certificate2 certificate)
        {
            // start fetching details about the subscriptions, available clusters, etc.
            _azureSubscriptions = new AzureSubscriptions();
            _azureSubscriptions.AddSubscription(subscriptionId, certificate);
            _cluster = _azureSubscriptions.GetClusterAsync(clusterName);
            _dfsClient = _cluster.ContinueWith(
                c => new AzureDfsClient(c.Result.StorageAccount, c.Result.StorageKey, "staging"));
        }

        /// <summary>
        /// Make a new cluster object representing an Azure HDInsight cluster, specifying the details
        /// manually.
        /// </summary>
        /// <param name="clusterName">The name of the HDInsight cluster</param>
        /// <param name="subscriptionId">The ID of the subscription to fetch cluster details from</param>
        /// <param name="certificateThumbprint">The thumbprint of the certificate associated with the subscription</param>
        /// <param name="storageAccount">The storage account to use for staging job resources</param>
        /// <param name="storageContainer">The storage account container to use for staging job resources</param>
        /// <param name="storageKey">The storage account key, which will be looked up in the subscription if null</param>
        public DryadLinqAzureCluster(string clusterName, string subscriptionId, string certificateThumbprint,
                                     string storageAccount, string storageContainer, string storageKey = null)
        {
            // start fetching details about the subscriptions, available clusters, etc.
            _azureSubscriptions = new AzureSubscriptions();
            if (storageKey != null)
            {
                _azureSubscriptions.AddAccount(storageAccount, storageKey);
            }
            _azureSubscriptions.AddCluster(clusterName, storageAccount, storageKey, subscriptionId, certificateThumbprint);
            _cluster = _azureSubscriptions.GetClusterAsync(clusterName);
            _dfsClient = _cluster.ContinueWith(
                 c => new AzureDfsClient(c.Result.StorageAccount, c.Result.StorageKey, storageContainer));
        }

        /// <summary>
        /// Make a new cluster object representing an Azure HDInsight cluster, specifying the details
        /// manually
        /// </summary>
        /// <param name="clusterName">The name of the HDInsight cluster</param>
        /// <param name="subscriptionId">The ID of the subscription to fetch cluster details from</param>
        /// <param name="certificate">The certificate associated with the subscription</param>
        /// <param name="storageAccount">The storage account to use for staging job resources</param>
        /// <param name="storageContainer">The storage account container to use for staging job resources</param>
        /// <param name="storageKey">The storage account key, which will be looked up in the subscription if null</param>
        public DryadLinqAzureCluster(string clusterName, string subscriptionId, X509Certificate2 certificate,
                                     string storageAccount, string storageContainer, string storageKey = null)
        {
            // start fetching details about the subscriptions, available clusters, etc.
            _azureSubscriptions = new AzureSubscriptions();
            if (storageKey != null)
            {
                _azureSubscriptions.AddAccount(storageAccount, storageKey);
            }
            _azureSubscriptions.AddCluster(clusterName, storageAccount, storageKey, subscriptionId, certificate);
            _cluster = _azureSubscriptions.GetClusterAsync(clusterName);
            _dfsClient = _cluster.ContinueWith(
                c => new AzureDfsClient(c.Result.StorageAccount, c.Result.StorageKey, storageContainer));
        }

        public PlatformKind Kind { get { return PlatformKind.YARN_AZURE; } }

        internal AzureSubscriptions Subscriptions { get { return _azureSubscriptions; } }

        public AzureCluster Cluster { get { return _cluster.Result; } }

        public DfsClient DfsClient { get { return _dfsClient.Result; } }

        public ClusterClient Client(DryadLinqContext context)
        {
            if (_clusterClient == null)
            {
                _clusterClient = _dfsClient.ContinueWith(
                    c => new AzureYarnClient(_azureSubscriptions, c.Result, MakeInternalClusterUri(""), context.PeloponneseHomeDirectory, Cluster.Name));
            }
            return _clusterClient.Result;
        }

        public Uri MakeInternalClusterUri(params string[] path)
        {
            return DfsClient.Combine(
                Microsoft.Research.Peloponnese.Azure.Utils.ToAzureUri(
                    _dfsClient.Result.AccountName, _dfsClient.Result.ContainerName, "", null, _dfsClient.Result.AccountKey),
                path);
        }

        /// <summary>
        /// The maximum number of nodes to use for a job if JobMinNodes is not set in DryadLinqContext
        /// </summary>
        public int DefaultMaxNodes { get { return -1; } }
    }

    /// <summary>
    /// Represents the context necessary to prepare and execute a DryadLinq Query,
    /// </summary>
    /// <remarks>
    /// <para>
    /// DryadLinqContext is the main entry point for the DryadLINQ framework.
    /// The context that is maintained by a DryadLinqContext instance includes
    /// configuration information.
    /// </para>
    /// <para>
    /// A DryadLinqContext may be reused by multiple queries and query executions.
    /// </para>
    /// <para>
    /// A DryadLinqContext may hold open connections to cluster services.
    /// To release these connections, call DryadLinqContext.Dispose().
    /// </para>
    /// </remarks>
    public class DryadLinqContext : IDisposable, IEquatable<DryadLinqContext>
    {
        private ExecutorKind _executorKind = ExecutorKind.DRYAD;
        private PlatformKind _platformKind = PlatformKind.LOCAL;

        private string _headNode;
        private DryadLinqCluster _clusterDetails;
        private AzureSubscriptions _azureSubscriptions;

        private Version _clientVersion;

        private CompressionScheme _intermediateDataCompressionScheme = CompressionScheme.None;
        private CompressionScheme _outputCompressionScheme = CompressionScheme.None;

        private bool _compileForVertexDebugging = false; // Ship PDBs + No optimization

        private string _jobFriendlyName;
        private int? _jobMinNodes;
        private int? _jobMaxNodes;
        private int _threadsPerWorker;
        private int _amMemoryMb;
        private int _containerMemoryMb;
        private string _queue = "default";
        private string _nodeGroup;
        private int? _jobRuntimeLimit;
        private bool _localDebug = false;
        private string _jobUsername = null;
        private string _jobPassword = null;
        private QueryLoggingLevel _runtimeLoggingLevel = QueryLoggingLevel.Error;
        private string _graphManagerNode;

        private bool _enableSpeculativeDuplication = true;
        private bool _selectOrderPreserving = false;
        private bool _matchClientNetFrameworkVersion = true;
        private string _partitionUncPath = null;
        private string _storageSetScheme = null;

        private DryadLinqStringDictionary _jobEnvironmentVariables = new DryadLinqStringDictionary();
        private DryadLinqStringList _resourcesToAdd = new DryadLinqStringList();
        private DryadLinqStringList _resourcesToRemove = new DryadLinqStringList();
        private bool _forceGC = false;
        private bool _isDisposed = false;

        private string _dryadHome;
        private string _peloponneseHome;

        private static DryadLinqCluster MakeCluster(string clusterName, PlatformKind kind)
        {
            return MakeCluster(clusterName, clusterName, kind);
        }

        private static DryadLinqCluster MakeCluster(string headNode, string nameNode, PlatformKind kind)
        {
            if (kind == PlatformKind.LOCAL)
            {
                throw new DryadLinqException("Can't make a cluster of kind LOCAL");
            }
            else if (kind == PlatformKind.YARN_NATIVE)
            {
                throw new DryadLinqException("Can't make a cluster of kind YARN_NATIVE: make a DryadLinqYarnCluster object");
            }
            else if (kind == PlatformKind.YARN_AZURE)
            {
                return new DryadLinqAzureCluster(headNode);
            }
            else
            {
                throw new DryadLinqException("Unknown cluster kind " + kind);
            }
        }

        /// <summary>
        /// Initializes a new instance of the DryadLinqContext class for local execution.
        /// </summary>
        /// <param name="numProcesses">The number of local worker processes that should be started.</param>
        /// <param name="storageSetScheme">The default scheme for storage. Defaults to partitioned file</param>
        public DryadLinqContext(int numProcesses, string storageSetScheme = null)
        {
            this.CommonInit();
            this._platformKind = PlatformKind.LOCAL;
            this._headNode = "LocalExecution";
            this._storageSetScheme = storageSetScheme;
            if (String.IsNullOrEmpty(this._storageSetScheme))
            {
                this._storageSetScheme = DataPath.PARTFILE_URI_SCHEME;
            }
            this._jobMaxNodes = numProcesses;
            this._threadsPerWorker = Environment.ProcessorCount;
            // make an Azure subscriptions object just in case we want to access azure streams from local execution
            this._azureSubscriptions = new AzureSubscriptions();
        }

        /// <summary>
        /// Initializes a new instance of the DryadLinqContext class for a YARN cluster.
        /// </summary>
        /// <param name="clusterName">The head node of the cluster and DFS</param>
        /// <param name="platform">The service platform to run DryadLINQ jobs. Defaults to YARN Azure</param>
        public DryadLinqContext(string clusterName, PlatformKind platform = PlatformKind.YARN_AZURE)
            : this(MakeCluster(clusterName, platform))
        {
        }

        /// <summary>
        /// Initializes a new instance of the DryadLinqContext class for a specified cluster.
        /// </summary>
        /// <param name="cluster">The cluster to run DryadLINQ jobs</param>
        public DryadLinqContext(DryadLinqCluster cluster)
        {
            // Verify that the head node is set
            if (String.IsNullOrEmpty(cluster.HeadNode))
            {
                throw new DryadLinqException(DryadLinqErrorCode.ClusterNameMustBeSpecified,
                                             SR.ClusterNameMustBeSpecified);
            }

            this.CommonInit();
            this._platformKind = cluster.Kind;
            this._headNode = cluster.HeadNode;
            this._clusterDetails = cluster;
            this._jobMaxNodes = cluster.DefaultMaxNodes;
            this._threadsPerWorker = Environment.ProcessorCount;

            if (cluster.Kind == DryadLinq.PlatformKind.YARN_NATIVE)
            {
                this._storageSetScheme = DataPath.HDFS_URI_SCHEME;
                // make an Azure subscriptions object just in case we want to access azure streams from the native yarn cluster
                this._azureSubscriptions = new AzureSubscriptions();
                DryadLinqYarnCluster yarnCluster = cluster as DryadLinqYarnCluster;
                if (yarnCluster == null)
                {
                    throw new DryadLinqException("Expected DryadLinqYarnCluster cluster object when running on YARN");
                }
                this._amMemoryMb = yarnCluster.ApplicationMasterMbMemory;
                this._containerMemoryMb = yarnCluster.ContainerMbMemory;
                this._queue = yarnCluster.Queue;
            }
            else if (cluster.Kind == DryadLinq.PlatformKind.YARN_AZURE)
            {
                this._storageSetScheme = DataPath.AZUREBLOB_URI_SCHEME;
                DryadLinqAzureCluster azureCluster = cluster as DryadLinqAzureCluster;
                this._azureSubscriptions = azureCluster.Subscriptions;
            }
        }

        private void CommonInit()
        {
            this._peloponneseHome = Peloponnese.ClusterUtils.ConfigHelpers.GetPPMHome(null);
            if (Microsoft.Research.Peloponnese.ClusterUtils.ConfigHelpers.RunningFromNugetPackage)
            {
                this._dryadHome = Microsoft.Research.Peloponnese.ClusterUtils.ConfigHelpers.GetPPMHome(null);
            }
            else
            {
                this._dryadHome = Environment.GetEnvironmentVariable(StaticConfig.DryadHomeVar);
            }
            this._amMemoryMb = -1;
            this._containerMemoryMb = -1;
        }

        /// <summary>
        /// Gets and sets the job executor. The current release only supports Dryad.
        /// </summary>
        public ExecutorKind ExecutorKind
        {
            get { return this._executorKind; }
            set { _executorKind = value; }
        }

        /// <summary>
        /// Gets or sets the service platform
        /// </summary>
        public PlatformKind PlatformKind
        {
            get { return _platformKind; }
            set { _platformKind = value; }
        }

        /// <summary>
        /// Gets or sets the value specifying whether data passed between stages will be compressed.
        /// </summary>
        /// <remarks>
        /// The default is true.
        /// </remarks>
        public CompressionScheme IntermediateDataCompressionScheme
        {
            get { return this._intermediateDataCompressionScheme; }
            set { this._intermediateDataCompressionScheme = value; }
        }

        /// <summary>
        /// Gets or sets the value specifying the compression scheme for output data.
        /// </summary>
        /// <remarks>
        /// The default is <see cref="CompressionScheme.None"/>.
        /// </remarks>
        public CompressionScheme OutputDataCompressionScheme
        {
            get { return this._outputCompressionScheme; }
            set { this._outputCompressionScheme = value; }
        }

        /// <summary>
        /// Gets or sets the value specifying whether to compile code with debugging support.
        /// </summary>
        /// <remarks>
        /// If true, vertex code will be compiled with no code-level optimizations and a PDB will be generated.
        /// Also, the query execution job look for and include the PDB associated with every DLL resource 
        /// that is part of the submitted job.
        /// <para>The default is false.</para>
        /// </remarks>
        public bool CompileForVertexDebugging
        {
            get { return _compileForVertexDebugging; }
            set { _compileForVertexDebugging = value; }
        }

        /// <summary>
        /// Gets or sets the bin directory for Dryad.
        /// </summary>
        public string DryadHomeDirectory
        {
            get { return _dryadHome; }
            set { _dryadHome = value; }
        }

        /// <summary>
        /// Gets or sets the bin directory for Peloponnese.
        /// </summary>
        public string PeloponneseHomeDirectory
        {
            get { return _peloponneseHome; }
            set { _peloponneseHome = value; }
        }

        /// <summary>
        /// Gets or sets the head node for executing a DryadLinq query.
        /// </summary>
        public string HeadNode
        {
            get { return _headNode; }
            set { _headNode = value; }
        }

        /// <summary>
        /// Gets or sets the partition UNC path used when constructing a partitioned table.
        /// </summary>
        public string PartitionUncPath
        {
            get { return _partitionUncPath; }
            set { _partitionUncPath = value; }
        }

        internal DfsClient GetHdfsClient
        {
            get
            {
                if (_clusterDetails != null && _clusterDetails is DryadLinqYarnCluster)
                {
                    return _clusterDetails.DfsClient;
                }
                if (Environment.GetEnvironmentVariable("HADOOP_COMMON_HOME") == null ||
                    Environment.GetEnvironmentVariable("JAVA_HOME") == null)
                {
                    return new WebHdfsClient(null, 50070);
                }
                else
                {
                    return new HdfsClient();
                }
            }
        }

        /// <summary>
        /// Gets the cluster object used to run the DryadLINQ query
        /// </summary>
        internal DryadLinqCluster Cluster
        {
            get { return _clusterDetails; }
        }

        /// <summary>
        /// Gets the collection of environment variables associated with the DryadLINQ job.
        /// </summary>
        public IDictionary<string, string> JobEnvironmentVariables
        {
            get { return _jobEnvironmentVariables; }
        }

        /// <summary>
        /// Gets or sets the descriptive name used to describe the DryadLINQ job.
        /// </summary>
        public string JobFriendlyName
        {
            get { return _jobFriendlyName; }
            set { _jobFriendlyName = value; }
        }

        /// <summary>
        /// Gets or sets the minimum number of cluster nodes for the DryadLINQ job.
        /// </summary>
        /// <remarks>
        /// <para>The default is null (no lower limit). May be overriden by cluster settings such as node templates.</para>
        /// </remarks>
        public int? JobMinNodes
        {
            get { return _jobMinNodes; }
            set { _jobMinNodes = value; }
        }

        /// <summary>
        /// Gets or sets the maximum number of cluster nodes for the DryadLINQ job.
        /// </summary>
        /// <remarks>
        /// <para>The default is null (no upper limit). May be overriden by cluster settings such as node templates.</para>
        /// </remarks>
        public int? JobMaxNodes
        {
            get { return _jobMaxNodes; }
            set { _jobMaxNodes = value; }
        }

        /// <summary>
        /// Gets or sets the number of threads each DryadLINQ worker vertex will use
        /// </summary>
        /// <remarks>
        /// <para>The default is 1.</para>
        /// </remarks>
        public int ThreadsPerWorker
        {
            get { return _threadsPerWorker; }
            set { _threadsPerWorker = value; }
        }

        /// <summary>
        /// Gets or sets the amount of memory in Megabytes requested for the Application Master container.
        /// </summary>
        public int ApplicationMasterMbMemory
        {
            get { return _amMemoryMb; }
            set { _amMemoryMb = value; }
        }

        /// <summary>
        /// Gets or sets the amount of memory in Megabytes requested for the worker containers.
        /// </summary>
        public int ContainerMbMemory
        {
            get { return _containerMemoryMb; }
            set { _containerMemoryMb = value; }
        }

        /// <summary>
        /// The Queue that should be used for job submission.  The default queue is the default value.
        /// </summary>
        public string Queue
        {
            get { return _queue; }
            set { _queue = value; }
        }

        /// <summary>
        /// Gets or sets the name of the compute node group when running on the cluster.
        /// </summary>
        /// <remarks>
        /// Creation and management of nodes groups is performed using the Cluster Manager.
        /// </remarks>
        /// <remarks>
        /// <para>The default is null (no node group restriction).</para>
        /// </remarks>
        public string NodeGroup
        {
            get { return _nodeGroup; }
            set { _nodeGroup = value; }
        }

        /// <summary>
        /// Gets or sets the maximum execution time for the DryadLINQ job, in seconds.
        /// </summary>
        /// <remarks>
        /// <para>The default is null (no runtime limit).</para>
        /// </remarks>
        public int? JobRuntimeLimit
        {
            get { return _jobRuntimeLimit; }
            set { _jobRuntimeLimit = value; }
        }

        /// <summary>
        /// Enables or disables speculative duplication of vertices based on runtime performance analysis. 
        /// </summary>
        /// <remarks>
        /// <para>The default is true.</para>
        /// </remarks>
        public bool EnableSpeculativeDuplication
        {
            get { return _enableSpeculativeDuplication; }
            set { _enableSpeculativeDuplication = value; }
        }

        /// <summary>
        /// Gets or sets the value specifying whether to use Local debugging mode.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If true, the DryadLINQ query will execute in the current CLR via LINQ-to-Objects.
        /// This mode is particularly useful for debugging user-functions before attempting cluster execution.
        /// LocalDebug mode accesses input and output data as usual.
        /// </para>
        /// <para>
        /// LocalDebug mode does not perform vertex-code compilation.
        /// </para>
        /// <para>The default is false.</para>
        /// </remarks>
        public bool LocalDebug
        {
            get { return _localDebug; }
            set { _localDebug = value; }
        }

        /// <summary>
        /// Gets and sets the value specifying whether a vertex should break into the debugger
        /// </summary>
        public bool DebugBreak
        {
            get
            {
                return this.JobEnvironmentVariables.ContainsKey("DLINQ_DEBUGVERTEX");
            }
            set
            {
                this.JobEnvironmentVariables["DLINQ_DEBUGVERTEX"] = "BREAK";
            }
        }

        /// <summary>
        /// Get the list of resources to add to the DryadLINQ job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// During query submission, some resources will be detected and added automatically.
        /// It is only necessary to add resources that are not detected automatically.
        /// </para>
        /// <para>
        /// Each resource should be a complete path to a file-based resource accessible
        /// from the local machine.
        /// </para>
        /// </remarks>
        public IList<string> ResourcesToAdd
        {
            get { return _resourcesToAdd; }
        }

        /// <summary>
        /// Get the list of resources to be excluded from the DryadLINQ job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// During query submission, some resources will be detected and added automatically.  
        /// Remove resources that are detected automatically but that are not required for job execution.
        /// </para>
        /// <para>
        /// Each resource should be a complete path to a file-based resource accessible from the local machine.
        /// </para>
        /// </remarks>
        public IList<string> ResourcesToRemove
        {
            get { return _resourcesToRemove; }
        }

        /// <summary>
        /// Gets or sets the RunAs password for jobs submitted to the cluster.
        /// </summary>
        /// <remarks>
        /// <para>The default is null (use the credentials of the current Thread)</para>
        /// </remarks>
        public string JobUsername
        {
            get { return _jobUsername; }
            set { _jobUsername = value; }
        }

        /// <summary>
        /// Gets or sets the RunAs password for jobs submitted to the cluster.
        /// </summary>
        /// <remarks>
        /// <para>The default is null (use the credentials of the current Thread)</para>
        /// </remarks>
        public string JobPassword
        {
            get { return _jobPassword; }
            set { _jobPassword = value; }
        }

        /// <summary>
        /// Gets or sets the logging level to use for DryadLINQ Query jobs.
        /// </summary>
        /// <remarks>
        /// <para>The RuntimeLoggingLevel affects the logs produced by all components associated with the execution
        /// of a DryadLINQ Query job.
        /// </para>
        /// <para>The default is QueryLoggingLevel.Error</para>
        /// </remarks>
        public QueryLoggingLevel RuntimeLoggingLevel
        {
            get { return _runtimeLoggingLevel; }
            set { _runtimeLoggingLevel = value; }
        }

        /// <summary>
        /// Gets or sets the node that should be used for running the Dryad Graph Manager task.
        /// </summary>
        /// <remarks>
        /// If null, the Graph Manager task will run on an arbitrary machine that is allocated to the DryadLINQ job.
        /// </remarks>
        public string GraphManagerNode
        {
            get { return _graphManagerNode; }
            set { _graphManagerNode = value; }
        }

        /// <summary>
        /// Gets or sets whether certain operators will preserve item ordering.
        /// When true, the Select, SelectMany and Where operators will preserve item ordering;
        /// otherwise, they may shuffle the input items as they are processed.
        /// </summary>
        public bool SelectOrderPreserving
        {
            get { return _selectOrderPreserving; }
            set { _selectOrderPreserving = value; }
        }

        /// <summary>
        /// Configures query jobs to be launched on the cluster nodes against a .NET framework version 
        /// matching that of the client process. This should only be set if all cluster nodes are known to have
        /// the same .NET version as the client. 
        /// When set to false (default), the vertex code will be compiled and run against .NET Framework 3.5.
        /// </summary>
        public bool MatchClientNetFrameworkVersion
        {
            get { return _matchClientNetFrameworkVersion; }
            set { _matchClientNetFrameworkVersion = value; }
        }

        /// <summary>
        /// Gets or sets whether to run GC after Moxie runs each task.
        /// </summary>
        /// <remarks>
        /// This only works with Moxie (for now at least).
        /// </remarks>
        public bool ForceGC
        {
            get { return _forceGC; }
            set { _forceGC = value; }
        }

        /// <summary>
        /// Version of the DryadLinq client components
        /// </summary>
        /// <returns>The version of the DryadLINQ DLL</returns>
        public Version ClientVersion()
        {
            ThrowIfDisposed();
            if (_clientVersion == null)
            {
                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    _clientVersion = new Version(FileVersionInfo.GetVersionInfo(asm.Location).FileVersion);
                }
                catch (Exception ex)
                {
                    throw new DryadLinqException(DryadLinqErrorCode.CouldNotGetClientVersion,
                                                 SR.CouldNotGetClientVersion, ex);
                }
            }
            return _clientVersion;
        }

        internal DryadLinqJobExecutor MakeJobExecutor()
        {
            switch (this.ExecutorKind)
            {
                case ExecutorKind.DRYAD:
                {
                    return new DryadLinqJobExecutor(this);
                }
                default:
                {
                    throw new Exception("No implementation for scheduler: " + this.ExecutorKind.ToString());
                }
            }
        }

        internal Uri MakeTemporaryStreamUri()
        {
            if (this._storageSetScheme == null)
            {
                throw new DryadLinqException("The storage scheme for temporary streams must be specified.");
            }
            DataProvider dataProvider = DataProvider.GetDataProvider(this._storageSetScheme);
            return dataProvider.GetTemporaryStreamUri(this, DryadLinqUtil.MakeUniqueName());
        }

        /// <summary>
        /// Open a dataset as a DryadLinq specialized IQueryable{T}.
        /// </summary>
        /// <typeparam name="T">The type of the records in the table</typeparam>
        /// <param name="dataSetUri">The name of the dataset</param>
        /// <param name="deserializer">The function to deserialize the input dataset</param>
        /// <returns>An IQueryable{T} representing the data</returns>
        public IQueryable<T> FromStore<T>(string dataSetUri,
                                          Expression<Func<Stream, IEnumerable<T>>> deserializer = null)
        {
            return this.FromStore<T>(new Uri(dataSetUri), deserializer);
        }

        /// <summary>
        /// Open a dataset as a DryadLinq specialized IQueryable{T}.
        /// </summary>
        /// <typeparam name="T">The type of the records in the table</typeparam>
        /// <param name="dataSetUri">The name of the dataset</param>
        /// <param name="deserializer">The function to deserialize the input dataset</param>
        /// <returns>An IQueryable{T} representing the data</returns>
        public IQueryable<T> FromStore<T>(Uri dataSetUri,
                                          Expression<Func<Stream, IEnumerable<T>>> deserializer = null)
        {
            ThrowIfDisposed();
            DryadLinqQuery<T> q = DataProvider.GetPartitionedTable<T>(this, dataSetUri, deserializer);
            q.CheckAndInitialize();   // Must initialize!
            return q;
        }

        /// <summary>
        /// Converts an IEnumerable{T} to a DryadLinq specialized IQueryable{T}.
        /// </summary>
        /// <typeparam name="T">The type of the records in the table.</typeparam>
        /// <param name="data">The source data.</param>
        /// <param name="serializer">An optional stream-based serializer</param>
        /// <param name="deserializer">An optional stream-based deserializer</param>
        /// <returns>An IQueryable{T} representing the data with DryadLinq query provider.</returns>
        /// <remarks>
        /// The source data will be serialized to a temp stream.
        /// The resulting fileset has an auto-generated name and a temporary lease.
        /// </remarks>
        public IQueryable<T> FromEnumerable<T>(IEnumerable<T> data,
                                               Expression<Action<IEnumerable<T>, Stream>> serializer = null,
                                               Expression<Func<Stream, IEnumerable<T>>> deserializer = null)
        {
            if ((serializer == null) ^ (deserializer == null))
            {
                throw new DryadLinqException("Must provide both serializer and deserializer.");
            }
            Uri dataSetName = this.MakeTemporaryStreamUri();
            CompressionScheme compressionScheme = this.OutputDataCompressionScheme;
            DryadLinqMetaData metadata = new DryadLinqMetaData(this, typeof(T), dataSetName, compressionScheme);
            return DataProvider.StoreData(this, data, dataSetName, metadata, compressionScheme,
                                          true, serializer, deserializer);
        }

        /// <summary>
        /// Register a named account with the specified storage key, so that key won't need to be specified in Azure blob URIs
        /// </summary>
        /// <param name="storageAccountName">The name of the storage account</param>
        /// <param name="storageAccountKey">The account's key</param>
        public void RegisterAzureAccount(string storageAccountName, string storageAccountKey)
        {
            _azureSubscriptions.AddAccount(storageAccountName, storageAccountKey);
        }

        /// <summary>
        /// Get the key associated with a named account, or null if it is not registered or auto-detected from
        /// the subscriptions
        /// </summary>
        /// <param name="storageAccountName">The name of the storage account</param>
        /// <returns>The storage key, or null</returns>
        public string AzureAccountKey(string storageAccountName)
        {
            return _azureSubscriptions.GetAccountKeyAsync(storageAccountName).Result;
        }

        internal static DryadLinqContext GetContext(IQueryProvider provider)
        {
            DryadLinqProviderBase baseProvider = provider as DryadLinqProviderBase;
            if (baseProvider == null)
            {
                throw new DryadLinqException("Must be DryadLINQ query provider.");
            }
            DryadLinqContext context = baseProvider.Context;
            context.ThrowIfDisposed();
            return context;
        }

        /// <summary>
        /// Releases all resources used by the DryadLinqContext.
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
            }
        }

        internal void ThrowIfDisposed()
        {
            if (this._isDisposed)
            {
                throw new DryadLinqException(DryadLinqErrorCode.ContextDisposed, SR.ContextDisposed);
            }
        }

        /// <summary>
        /// Determines whether this instance of DryadLinqContext is equal to another instance 
        /// of <see cref="DryadLinqContext"/>.
        /// </summary>
        /// <param name="context">The other DryadLinqContext instance</param>
        /// <returns>true if the two instances are equal</returns>
        public virtual bool Equals(DryadLinqContext context)
        {
            return (this.IntermediateDataCompressionScheme == context.IntermediateDataCompressionScheme &&
                    this.OutputDataCompressionScheme == context.OutputDataCompressionScheme &&
                    this.CompileForVertexDebugging == context.CompileForVertexDebugging &&
                    this.DryadHomeDirectory == context.DryadHomeDirectory &&
                    this.PeloponneseHomeDirectory == context.PeloponneseHomeDirectory &&
                    this.HeadNode == context.HeadNode &&
                    this.Cluster == context.Cluster &&
                    this.PartitionUncPath == context.PartitionUncPath &&
                    this.JobMinNodes == context.JobMinNodes &&
                    this.JobMaxNodes == context.JobMaxNodes &&
                    this.NodeGroup == context.NodeGroup &&
                    this.JobRuntimeLimit == context.JobRuntimeLimit &&
                    this.EnableSpeculativeDuplication == context.EnableSpeculativeDuplication &&
                    this.LocalDebug == context.LocalDebug &&
                    this.PlatformKind == context.PlatformKind &&
                    this.JobUsername == context.JobUsername &&
                    this.JobPassword == context.JobPassword &&
                    this.RuntimeLoggingLevel == context.RuntimeLoggingLevel &&
                    this.GraphManagerNode == context.GraphManagerNode &&
                    this.SelectOrderPreserving == context.SelectOrderPreserving &&
                    this.MatchClientNetFrameworkVersion == context.MatchClientNetFrameworkVersion &&
                    this.ThreadsPerWorker == context.ThreadsPerWorker &&
                    this.ForceGC == context.ForceGC);
        }
    }
}
