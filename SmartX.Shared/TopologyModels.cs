namespace SmartX.Shared;

// ============================================================================
//  Final PoE — Network Topology & Mesh Routing: shared contracts between the
//  API, the Blazor client and the ESP32 fleet simulator.
// ============================================================================

/// <summary>
/// Role of a node in the mesh. The gateway is the root (ESP-MESH "root node"); a node that
/// forwards packets for other nodes on its route to the gateway is a Router; a node that
/// only sends its own packets is a Leaf.
/// </summary>
public enum MeshRole { Gateway = 0, Router = 1, Leaf = 2 }

/// <summary>Why a node is (or is not) part of the routable mesh right now.</summary>
public enum MeshNodeState { Online = 0, Offline = 1, Isolated = 2, Unreachable = 3 }

/// <summary>One vertex of the mesh graph, with its position on the farm map (metres).</summary>
public record MeshNodeDto(
    string MacAddress, string DisplayName, string Category, string Zone, MeshRole Role, MeshNodeState State,
    double X, double Y, int? HopCount, string? ParentMac, double? RouteCost,
    int Degree, bool IsArticulationPoint, int NodesCutOffIfLost);

/// <summary>One undirected edge (wireless link). Cost = relative radio energy per delivered packet.</summary>
public record MeshLinkDto(
    string A, string B, double DistanceMeters, int Rssi, double Cost,
    bool IsActive, bool IsFailed, bool InMst);

/// <summary>Headline numbers for the topology dashboard.</summary>
public record TopologySummary(
    int NodeCount, int OnlineCount, int ReachableCount, int LinkCount, int ActiveLinkCount,
    int ComponentCount, int MaxHops, List<string> UnreachableMacs, List<string> DownMacs,
    List<string> ArticulationPoints,
    double TotalActiveLinkCost, double MstCost, int MstEdgeCount, double SavingsPercent,
    double PrimCost, double KruskalCost, bool PrimKruskalAgree,
    long HistoryPointCount, int HistorySensorCount);

/// <summary>A rectangular area of the farm map, in metres (drawn behind the mesh on the topology page).</summary>
public record MeshZoneDto(string Name, double X, double Y, double Width, double Height);

/// <summary>Everything the topology page needs, pushed over SignalR ("topology") once per second.</summary>
public record TopologySnapshot(
    string GatewayMac, double MapWidth, double MapHeight, List<MeshZoneDto> Zones,
    List<MeshNodeDto> Nodes, List<MeshLinkDto> Links, TopologySummary Summary,
    List<string> RecentEvents, DateTime GeneratedUtc);

/// <summary>One hop on a route: the link that was used and what it cost.</summary>
public record RouteHop(string FromMac, string FromName, string ToMac, string ToName, double Cost, int Rssi);

/// <summary>
/// How telemetry travels from one node to another (normally device → gateway).
/// MstPath is the route along the minimum spanning tree — the power-efficient backbone the mesh routes on.
/// ShortestPath is the cheapest single path Dijkstra finds (a binary-heap search), for comparison.
/// BackupPath is the best path that avoids the first relay: where the packet hops if that relay goes down.
/// </summary>
public record RouteResult(
    string FromMac, string ToMac, bool Found, string Message,
    List<string> MstPath, List<string> MstPathNames, List<RouteHop> Hops, double MstCost,
    List<string> ShortestPath, List<string> ShortestPathNames, double ShortestCost,
    List<string>? BackupPath, List<string>? BackupPathNames, double? BackupCost, int NodesSettled);

/// <summary>One visit in a BFS/DFS traversal.</summary>
public record TraversalStep(int Order, string MacAddress, string DisplayName, int Depth, string? ParentMac, bool IsDown);

/// <summary>
/// Result of BFS or DFS from a start node. Visited = reachable devices on that subnet;
/// Down = registered devices that could not be reached, each with a suggested next hop if one exists.
/// </summary>
public record TraversalResult(
    string Algorithm, string StartMac, string StartName, List<TraversalStep> Order, int VisitedCount,
    List<DownDeviceAdvice> Down, string DataStructure);

/// <summary>Self-healing advice for a node that is cut off or offline.</summary>
public record DownDeviceAdvice(string MacAddress, string DisplayName, string Reason, string? SuggestedHopMac, string? SuggestedHopName);

/// <summary>An MST computed by Prim's or Kruskal's algorithm. Edges are listed in the order the algorithm accepted them.</summary>
public record MstResult(
    string Algorithm, List<MeshLinkDto> Edges, double TotalCost, int EdgeCount, int VertexCount,
    int ComponentCount, double AllLinksCost, double SavingsPercent, string DataStructure, double ElapsedMicroseconds);

/// <summary>Body of POST /api/topology/links/toggle.</summary>
public record LinkToggleRequest(string A, string B);

/// <summary>Result of an operator action on the mesh (fail/restore a link or a node).</summary>
public record TopologyActionResult(bool Success, string Message);

/// <summary>What an ESP32 reports about its neighbours (ESP-NOW neighbour table): MAC + received signal strength.</summary>
public record NeighborRssi(string NeighborMac, int Rssi);
public record NeighborReport(string MacAddress, List<NeighborRssi> Neighbors);

/// <summary>
/// Range query over a sensor's AVL-indexed history. NodesVisited shows how little of the tree was touched
/// compared with LinearComparisons (what a scan of an unordered list would cost).
/// </summary>
public record HistoryQueryResult(
    string MacAddress, string DisplayName, string Unit, DateTime FromUtc, DateTime ToUtc,
    int Count, double? Min, double? Max, double? Average,
    List<TimelinePoint> Points, bool Truncated,
    int TreeSize, int TreeHeight, int NodesVisited, long LinearComparisons, double ElapsedMicroseconds);

/// <summary>Size and shape of one sensor's AVL history tree.</summary>
public record HistoryIndexInfo(string MacAddress, string DisplayName, string Unit, int Size, int Height,
                               DateTime? OldestUtc, DateTime? NewestUtc);

/// <summary>Benchmark comparing the AVL range query with a linear scan, and graph algorithms on a large synthetic mesh.</summary>
public record ScaleBenchmarkResult(
    int HistoryPoints, int AvlHeight, int QueryMatches,
    double AvlQueryMicroseconds, double LinearScanMicroseconds, int AvlNodesVisited, long LinearComparisons,
    int MeshNodes, int MeshLinks, double BfsMilliseconds, double PrimMilliseconds, double KruskalMilliseconds,
    double DijkstraMilliseconds, bool PrimKruskalAgree);
