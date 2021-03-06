﻿module CGraph

open Util.Error
open Util.Debug
open Util.Format
open System
open System.Collections.Generic
open System.Diagnostics
open QuickGraph
open QuickGraph.Algorithms


[<CustomEquality; CustomComparison>]
type CgState =
    {Id: int; 
     State: int;
     Accept: Bitset32.T; 
     Node: Topology.Node}

    override this.ToString() = 
       "(Id=" + string this.Id + ", State=" + string this.State + ", Loc=" + this.Node.Loc + ")"

    override x.Equals(other) =
       match other with
        | :? CgState as y -> (x.Id = y.Id)
        | _ -> false
 
    override x.GetHashCode() = x.Id
     
    interface System.IComparable with
        member x.CompareTo other =
          match other with
          | :? CgState as y -> x.Id - y.Id
          | _ -> failwith "cannot compare values of different types"


type CgStateTmp =
    {TStates: int array;
     TNode: Topology.Node; 
     TAccept: Bitset32.T}


type T = 
    {Start: CgState;
     End: CgState;
     Graph: BidirectionalGraph<CgState, Edge<CgState>>
     Topo: Topology.T}
   

type Direction = 
  | Up
  | Down


let copyGraph (cg: T) : T = 
    let newCG = QuickGraph.BidirectionalGraph()
    for v in cg.Graph.Vertices do 
        newCG.AddVertex v |> ignore
    for e in cg.Graph.Edges do
        newCG.AddEdge e |> ignore
    {Start=cg.Start; Graph=newCG; End=cg.End; Topo=cg.Topo}


let copyReverseGraph (cg: T) : T = 
    let newCG = QuickGraph.BidirectionalGraph() 
    for v in cg.Graph.Vertices do newCG.AddVertex v |> ignore
    for e in cg.Graph.Edges do
        let e' = Edge(e.Target, e.Source)
        newCG.AddEdge e' |> ignore
    {Start=cg.Start; Graph=newCG; End=cg.End; Topo=cg.Topo}


let index ((graph, topo, startNode, endNode): BidirectionalGraph<_, _> * _ * _ * _) =
    let newCG = QuickGraph.BidirectionalGraph()
    let reindex = Util.Reindexer(HashIdentity.Structural)
    let nstart = {Id=0; Node=startNode.TNode; State=(reindex.Index startNode.TStates); Accept=startNode.TAccept}
    let nend = {Id=1; Node=endNode.TNode; State=(reindex.Index endNode.TStates); Accept=endNode.TAccept}
    ignore (newCG.AddVertex nstart)
    ignore (newCG.AddVertex nend)
    let mutable i = 2
    let idxMap = Dictionary(HashIdentity.Structural)
    idxMap.[(nstart.Node.Loc, nstart.State)] <- nstart
    idxMap.[(nend.Node.Loc, nend.State)] <- nend
    for v in graph.Vertices do
        if Topology.isTopoNode v.TNode then
            let newv = {Id=i; Node=v.TNode; State = (reindex.Index v.TStates); Accept=v.TAccept}
            i <- i + 1
            idxMap.Add( (v.TNode.Loc, reindex.Index v.TStates), newv)
            newCG.AddVertex newv |> ignore
    for e in graph.Edges do
        let v = e.Source 
        let u = e.Target
        let x = idxMap.[(v.TNode.Loc, reindex.Index v.TStates)]
        let y = idxMap.[(u.TNode.Loc, reindex.Index u.TStates)]
        newCG.AddEdge (Edge(x,y)) |> ignore
    {Start=nstart; Graph=newCG; End=nend; Topo=topo}


let getTransitions autos =
    let aux (auto: Regex.Automaton) = 
        let trans = Dictionary(HashIdentity.Structural)
        for kv in auto.trans do 
            let (q1,S) = kv.Key 
            let q2 = kv.Value 
            for s in S do 
                trans.[(q1,s)] <- q2
        trans
    Array.map aux autos


let getGarbageStates (auto: Regex.Automaton) = 
    let inline aux (kv: KeyValuePair<_,_>) = 
        let k = kv.Key
        let v = kv.Value 
        let c = Set.count v
        if c = 1 && Set.minElement v = k 
            then Some k 
            else None 
    let selfLoops = 
        Map.fold (fun acc (x,_) y  ->
            let existing = Util.Map.getOrDefault x Set.empty acc
            Map.add x (Set.add y existing) acc ) Map.empty auto.trans
            |> Seq.choose aux
            |> Set.ofSeq
    Set.difference selfLoops auto.F


let inline uniqueNeighbors canOriginate topo (t: Topology.Node) = 
    (if t.Typ = Topology.Start then canOriginate else Topology.neighbors topo t) |> Set.ofSeq


let buildFromAutomata (topo: Topology.T) (autos : Regex.Automaton array) : T =
    if autos.Length > 31 then 
        error (sprintf "Propane does not currently support more than 31 preferences")
    if not (Topology.isWellFormed topo) then
        error (sprintf "Invalid topology. Topology must be connected.")
    let unqTopo = Set.ofSeq (Topology.vertices topo)
    let transitions = getTransitions autos
    let garbage = Array.map getGarbageStates autos
    let graph = BidirectionalGraph<CgStateTmp, Edge<CgStateTmp>>()
    let starting = Array.map (fun (x: Regex.Automaton) -> x.q0) autos
    let newStart = {TStates = starting; TAccept = Bitset32.empty; TNode = Topology.Node("start", Topology.Start) }
    ignore (graph.AddVertex newStart)
    let marked = HashSet(HashIdentity.Structural)
    let todo = Queue()
    todo.Enqueue newStart
    while todo.Count > 0 do
        let currState = todo.Dequeue()
        let t = currState.TNode
        let canOrigin = Seq.filter Topology.canOriginateTraffic unqTopo 
        let adj = uniqueNeighbors canOrigin topo t
        let adj = if t.Typ = Topology.Unknown then Set.add t adj else adj
        for c in adj do
            let dead = ref true
            let nextInfo = Array.init autos.Length (fun i ->
                let g, v = autos.[i], currState.TStates.[i]
                let newState = transitions.[i].[(v,c.Loc)]
                if not (garbage.[i].Contains newState) then 
                    dead := false
                let accept =
                    if (Topology.canOriginateTraffic c) && (Set.contains newState g.F) 
                        then Bitset32.singleton (i+1)
                        else Bitset32.empty
                newState, accept)
            let nextStates, nextAccept = Array.unzip nextInfo
            let accept = Array.fold Bitset32.union Bitset32.empty nextAccept
            let state = {TStates=nextStates; TAccept=accept; TNode=c}
            if not !dead then
                if not (marked.Contains state) then
                    ignore (marked.Add state)
                    ignore (graph.AddVertex state) 
                    todo.Enqueue state 
                let edge = Edge(currState, state)
                ignore (graph.AddEdge edge)
    let newEnd = {TStates = [||]; TAccept = Bitset32.empty; TNode = Topology.Node("end", Topology.End)}
    graph.AddVertex newEnd |> ignore
    for v in graph.Vertices do
        if not (Bitset32.isEmpty v.TAccept) then
            let e = Edge(v, newEnd)
            ignore (graph.AddEdge(e))
    index (graph, topo, newStart, newEnd)


let inline loc x = x.Node.Loc

let inline shadows x y = (loc x = loc y) && (x <> y)


let inline preferences (cg: T) : Bitset32.T = 
    let mutable all = Bitset32.empty
    for v in cg.Graph.Vertices do 
        all <- Bitset32.union all v.Accept
    all


let inline acceptingStates (cg: T) : Set<CgState> =
    cg.Graph.Vertices
    |> Seq.filter (fun (v: CgState) -> not (Bitset32.isEmpty v.Accept))
    |> Set.ofSeq


let inline acceptingLocations (cg: T) : Set<string> = 
    acceptingStates cg
    |> Set.map loc


let inline isRealNode (state: CgState) = 
  Topology.isTopoNode state.Node


let inline neighbors (cg: T) (state: CgState) =
    seq {for e in cg.Graph.OutEdges state do yield e.Target}


let inline neighborsIn (cg: T) (state: CgState) =
    seq {for e in cg.Graph.InEdges state do yield e.Source}


let inline isRepeatedOut (cg: T) (state: CgState) =
    (state.Node.Typ = Topology.Unknown) &&
    (Seq.contains state (neighbors cg state))


let inline isInside x = Topology.isInside x.Node

let inline isOutside x = Topology.isOutside x.Node

let inline isEmpty (cg: T) = cg.Graph.VertexCount = 2


let restrict (cg: T) (i: int) = 
    if Bitset32.contains i (preferences cg) then 
        let copy = copyGraph cg
        copy.Graph.RemoveVertexIf (fun v -> 
            if not (Bitset32.isEmpty v.Accept) then 
                match Bitset32.minimum v.Accept with 
                | None -> true
                | Some j -> j > i
            else false) |> ignore
        copy
    else cg


let toDot (cg: T) (pi: Ast.PolInfo option) : string = 
    let onFormatEdge(e: Graphviz.FormatEdgeEventArgs<CgState, Edge<CgState>>) = ()
    let onFormatVertex(v: Graphviz.FormatVertexEventArgs<CgState>) = 
        let state = string v.Vertex.State
        let location = 
            match pi with 
            | None -> loc v.Vertex 
            | Some pi -> 
                let ti = pi.Ast.TopoInfo
                Topology.router (loc v.Vertex) ti
        match v.Vertex.Node.Typ with 
        | Topology.Start -> v.VertexFormatter.Label <- "Start"
        | Topology.End -> v.VertexFormatter.Label <- "End"
        | _ ->
            if Bitset32.isEmpty v.Vertex.Accept then 
                let label = sprintf "%s, %s" state location
                v.VertexFormatter.Label <- label
            else
                let accept = (Bitset32.toSet v.Vertex.Accept |> Util.Set.toString)
                let label = sprintf "%s, %s\nAccept=%s" state location accept
                v.VertexFormatter.Label <- label
                v.VertexFormatter.Shape <- Graphviz.Dot.GraphvizVertexShape.DoubleCircle
                v.VertexFormatter.Style <- Graphviz.Dot.GraphvizVertexStyle.Filled
                v.VertexFormatter.FillColor <- Graphviz.Dot.GraphvizColor.LightYellow
    let graphviz = Graphviz.GraphvizAlgorithm<CgState, Edge<CgState>>(cg.Graph)
    graphviz.FormatEdge.Add(onFormatEdge)
    graphviz.FormatVertex.Add(onFormatVertex)
    graphviz.Generate()


let generatePNG (cg: T) pi (file: string) : unit =
    System.IO.File.WriteAllText(file + ".dot", toDot cg pi)
    let p = new Process()
    p.StartInfo.FileName <- "dot"
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.Arguments <- "-Tpng " + file + ".dot -o " + file + ".png" 
    p.StartInfo.CreateNoWindow <- true
    p.Start() |> ignore
    p.WaitForExit();


module Reachable =

    let postOrder (cg: T) (source: CgState) direction : List<CgState> = 
        let f = if direction = Up then neighborsIn else neighbors
        let marked = HashSet()
        let ret = ResizeArray()
        let s = Stack()
        s.Push(source)
        while s.Count > 0 do 
            let v = s.Pop()
            if not (marked.Contains v) then 
                ignore (marked.Add v)
                ret.Add(v)
                for w in f cg v do 
                    s.Push(w)
        ret

    let dfs (cg: T) (source: CgState) direction : HashSet<CgState> = 
        let f = if direction = Up then neighborsIn else neighbors
        let marked = HashSet()
        let s = Stack()
        s.Push(source)
        while s.Count > 0 do 
            let v = s.Pop()
            if not (marked.Contains v) then 
                ignore (marked.Add v)
                for w in f cg v do 
                    s.Push(w)
        marked

    let inline srcAccepting cg src direction = 
        let reach = dfs cg src direction
        let mutable acc = Bitset32.empty
        for v in reach do 
            acc <- Bitset32.union v.Accept acc
        acc


module Domination = 

    type DomTreeMapping = Dictionary<CgState, CgState option>

    [<Struct>]
    type DominationTree(tree: DomTreeMapping) =
       
        member this.IsDominatedBy(x,y) = 
            match tree.[x] with 
            | None -> false
            | Some v -> 
                let mutable runner = x
                let mutable current = v
                let mutable found = false
                while not found && runner <> current do 
                    if runner = y then 
                        found <- true
                    runner <- current
                    current <- tree.[runner].Value
                found

        member this.IsDominatedByFun(x,f) = 
            match tree.[x] with 
            | None -> false
            | Some v -> 
                let mutable runner = x
                let mutable current = v
                let mutable found = false
                while not found && runner <> current do 
                    if f runner then 
                        found <- true
                    runner <- current
                    current <- tree.[runner].Value
                found

        member this.TryIsDominatedBy(x,f) = 
            match tree.[x] with 
            | None -> None
            | Some v -> 
                let mutable runner = x
                let mutable current = v
                let mutable found = None
                while Option.isNone found && runner <> current do 
                    if f runner then 
                        found <- Some runner
                    runner <- current
                    current <- tree.[runner].Value
                found

    let inter (po: Dictionary<CgState,int>) (dom: DomTreeMapping) b1 b2 =
        let mutable finger1 = b1
        let mutable finger2 = b2
        let mutable x = po.[finger1]
        let mutable y = po.[finger2] 
        while x <> y do 
            while x > y do 
                finger1 <- Option.get dom.[finger1]
                x <- po.[finger1]
            while y > x do 
                finger2 <- Option.get dom.[finger2]
                y <- po.[finger2]
        finger1

    let dominators (cg: T) root direction : DominationTree =
        let adj = if direction = Up then neighbors cg else neighborsIn cg
        let dom = Dictionary()
        let reach = Reachable.postOrder cg root direction
        let postorder = Seq.mapi (fun i n -> (n,i)) reach
        let postorderMap = Dictionary()
        for (n,i) in postorder do
            postorderMap.[n] <- i
        for b in cg.Graph.Vertices do 
            dom.[b] <- None
        dom.[root] <- Some root
        let mutable changed = true
        while changed do 
            changed <- false 
            for b, i in postorder do
                if b <> root then 
                    let preds = adj b
                    let mutable newIDom = Seq.find (fun x -> postorderMap.[x] < i) preds
                    for p in preds do
                        if p <> newIDom then 
                            if dom.[p] <> None then 
                                newIDom <- inter postorderMap dom p newIDom
                    let x = dom.[b]
                    if Option.isNone x || x.Value <> newIDom then
                        dom.[b] <- Some newIDom
                        changed <- true
        DominationTree(dom)


module Minimize =

    type Edge = struct 
        val X: int
        val Y: int
        new(x,y) = {X=x; Y=y}
    end

    let edgeSet (cg: T) = 
        let acc = HashSet()
        for e in cg.Graph.Edges do 
            let e = Edge(e.Source.Id, e.Target.Id)
            ignore (acc.Add e)
        acc

    let removeDominated (cg: T) =
        let dom = Domination.dominators cg cg.Start Down
        let domRev = Domination.dominators cg cg.End Up
        cg.Graph.RemoveVertexIf (fun v ->
            (not (isRepeatedOut cg v)) &&
            (dom.IsDominatedByFun(v, shadows v) || 
             domRev.IsDominatedByFun(v, shadows v))) |> ignore
        let edges = edgeSet cg
        cg.Graph.RemoveEdgeIf (fun e -> 
            let x = e.Source 
            let y = e.Target
            let e = Edge(y.Id,x.Id)
            (edges.Contains e) &&
            (not (isRepeatedOut cg x || isRepeatedOut cg y)) &&
            (dom.IsDominatedBy(x, y) || domRev.IsDominatedBy(y, x) ) &&
            (x <> y) ) |> ignore
        cg.Graph.RemoveEdgeIf (fun e ->
            let x = e.Source
            let y = e.Target
            (not (isRepeatedOut cg e.Source || isRepeatedOut cg e.Target)) &&
            (domRev.IsDominatedByFun(y, shadows x)) ) |> ignore

    let removeNodesThatCantReachEnd (cg: T) = 
        let canReach = Reachable.dfs cg cg.End Up
        cg.Graph.RemoveVertexIf(fun v -> 
            Topology.isTopoNode v.Node && not (canReach.Contains v)) |> ignore
        
    let removeNodesThatStartCantReach (cg: T) = 
        let canReach = Reachable.dfs cg cg.Start Down
        cg.Graph.RemoveVertexIf(fun v -> 
            Topology.isTopoNode v.Node && not (canReach.Contains v)) |> ignore

    let delMissingSuffixPaths cg = 
        let starting = neighbors cg cg.Start |> Seq.filter isRealNode |> Set.ofSeq
        cg.Graph.RemoveVertexIf (fun v -> 
            v.Node.Typ = Topology.InsideOriginates && 
            Bitset32.isEmpty v.Accept && 
            not (Set.contains v starting) ) |> ignore
        cg

    let inline allConnected cg outStar scc = 
        Set.forall (fun x -> 
            let nOut = Set.ofSeq (neighbors cg x)
            let nIn = Set.ofSeq (neighborsIn cg x)
            x = outStar || 
            (nOut.Contains outStar && nIn.Contains outStar) ) scc

    let removeConnectionsToOutStar (cg: T) = 
        cg.Graph.RemoveEdgeIf (fun e -> 
            let x = e.Source 
            let y = e.Target 
            let realNodes = isRealNode x && isRealNode y
            if realNodes then 
                if isRepeatedOut cg x then Seq.exists isInside (neighborsIn cg y)
                else if isRepeatedOut cg y then
                    Seq.exists isInside (neighbors cg x) && 
                    (Seq.exists ((=) cg.Start) (neighborsIn cg y) || 
                     Seq.forall ((<>) cg.Start) (neighborsIn cg x))
                else false
            else false) |> ignore

    let removeRedundantExternalNodes (cg: T) =
        let toDelNodes = HashSet(HashIdentity.Structural)
        let outside = 
            cg.Graph.Vertices 
            |> Seq.filter (isRepeatedOut cg)
            |> Set.ofSeq
        for os in outside do 
            let nos = Set.ofSeq (neighborsIn cg os)
            for n in Set.remove os nos do 
                if cg.Graph.OutDegree n = 1 && isOutside n then 
                    let nin = Set.ofSeq (neighborsIn cg n)
                    if Set.isSuperset nos nin then 
                        ignore (toDelNodes.Add n)
        for os in outside do 
            let nos = Set.ofSeq (neighbors cg os)
            for n in Set.remove os nos do 
                if cg.Graph.InDegree n = 1 && isOutside n then
                    let nin = Set.ofSeq (neighbors cg n)
                    if Set.isSuperset nos nin then 
                        ignore (toDelNodes.Add n)
        cg.Graph.RemoveVertexIf (fun v -> toDelNodes.Contains v) |> ignore
          
    let minimize (idx: int) (cg: T) =
        let settings = Args.getSettings ()
        let isConcrete = not settings.IsAbstract
        logInfo(idx, sprintf "Node count: %d" cg.Graph.VertexCount)
        let inline count cg = 
            cg.Graph.VertexCount + cg.Graph.EdgeCount
        let inline prune () = 
            removeNodesThatCantReachEnd cg
            logInfo(idx, sprintf "Node count (cant reach end): %d" cg.Graph.VertexCount)
            removeRedundantExternalNodes cg
            logInfo(idx, sprintf "Node count (redundant external nodes): %d" cg.Graph.VertexCount)
            removeConnectionsToOutStar cg
            logInfo(idx, sprintf "Node count (connections to out*): %d" cg.Graph.VertexCount)
            if isConcrete then
                removeDominated cg
                logInfo(idx, sprintf "Node count (remove dominated): %d" cg.Graph.VertexCount)
            removeNodesThatStartCantReach cg
            logInfo(idx, sprintf "Node count (start cant reach): %d" cg.Graph.VertexCount)
        let mutable sum = count cg
        prune() 
        while count cg <> sum do
            sum <- count cg
            prune ()
        logInfo(idx, sprintf "Node count - after O3: %d" cg.Graph.VertexCount)
        cg


module Consistency = 

    exception SimplePathException of CgState * CgState
    exception ConsistencyException of CgState * CgState

    type CounterExample =  CgState * CgState
    type Preferences = seq<CgState>
    type Ordering = Dictionary<string, Preferences>
    type Constraints = BidirectionalGraph<CgState ,Edge<CgState>>

    type CacheEntry = struct 
        val X: int 
        val Y: int
        new(x,y) = {X=x; Y=y}
    end

    type Node = struct 
        val More: CgState
        val Less: CgState
        new(m, l) = {More=m; Less=l}
    end

    type ProtectResult =
        | Yes of HashSet<Node>
        | No

    let protect (idx: int) (doms: Domination.DominationTree) cg n1 n2 : ProtectResult = 
        if loc n1 <> loc n2 then No else
        let q = Queue()
        let seen = HashSet()
        let counterEx = ref None
        // add nodes if preserves the preference relation
        let inline add x' y' (x,y) = 
            match Bitset32.minimum x'.Accept, Bitset32.minimum y'.Accept with
            | None, Some _
            | Some _, None -> counterEx := Some (x,y)
            | Some i, Some j when i > j -> counterEx := Some (x,y)
            | _,_ ->
                let n' = Node(x',y')
                if not (seen.Contains n') then 
                    ignore (seen.Add n')
                    q.Enqueue n'
        // add initial node
        add n1 n2 (n1,n2)
        while q.Count > 0 && Option.isNone !counterEx do
            let n = q.Dequeue()
            let x = n.More 
            let y = n.Less 
            let nsx = neighbors cg x |> Seq.fold (fun acc x -> Map.add (loc x) x acc) Map.empty
            let nsy = neighbors cg y
            for y' in nsy do 
                match Map.tryFind (loc y') nsx with
                | None ->
                    let inline relevantDom x' = loc x' = loc y' && cg.Graph.ContainsVertex x'
                    match doms.TryIsDominatedBy(x, relevantDom) with
                    | None -> counterEx := Some (x,y)
                    | Some x' -> add x' y' (x,y)
                | Some x' -> add x' y' (x,y)
        match !counterEx with 
        | None -> Yes seen
        | Some cex -> No


    let getDuplicateNodes (cg: T) = 
        let ret = Dictionary() 
        for v in cg.Graph.Vertices do 
            let l = loc v
            let mutable value = Set.empty 
            if ret.TryGetValue(l, &value) then 
                ret.[l] <- Set.add v value 
            else ret.[l] <- Set.singleton v
        Util.Dictionary.filter (fun _ v -> Set.count v > 1) ret

    let allDisjoint (cg: T) (dups: Dictionary<_,_>) = 
        let components = Dictionary() :> IDictionary<CgState,int>
        cg.Graph.WeaklyConnectedComponents(components) |> ignore
        dups |> Seq.forall (fun kv -> 
            let szInit = Set.count kv.Value
            let szFinal = Set.map (fun x -> components.[x]) kv.Value |> Set.count
            szInit = szFinal)

    let getHardPreferences (cg: T) = 
        let cg = copyGraph cg
        cg.Graph.RemoveVertexIf (fun v -> isOutside v || not (isRealNode v)) |> ignore
        let dups = getDuplicateNodes cg
        if dups.Count = 0 || allDisjoint cg dups then
            Dictionary()
        else 
            let dups = Util.Dictionary.fold (fun acc _ v -> Set.union acc v) Set.empty dups
            let mustPrefer = Dictionary()
            for d in dups do 
                let reach = Reachable.dfs cg d Down
                let below = Util.HashSet.filter (shadows d) reach
                if below.Count > 0 then
                    mustPrefer.[d] <- below
            mustPrefer

    let simulate idx cg (cache: HashSet<_>) (doms: Domination.DominationTree) (x,y) =
        let ce = CacheEntry(x.Id,y.Id)
        if cache.Contains ce then true else
        match protect idx doms cg x y with 
        | No -> false
        | Yes related -> 
            for n in related do 
                let ce = CacheEntry(n.More.Id, n.Less.Id)
                ignore (cache.Add ce)
            true

    let isPreferred idx cg cache doms (x,y) =
        simulate idx cg cache doms (x,y)

    let checkIncomparableNodes (g: Constraints) edges = 
        for x in g.Vertices do
            for y in g.Vertices do
                if x <> y && not (Set.contains (x,y) edges || Set.contains (y,x) edges) then
                    raise (ConsistencyException(x,y))

    let removeUnconstrainedEdges (g: Constraints) edges =
        let both = Set.filter (fun (x,y) -> Set.exists (fun (a,b) -> x=b && y=a) edges) edges
        g.RemoveEdgeIf (fun e -> Set.contains (e.Source, e.Target) both) |> ignore

    let getOrdering (g: Constraints) edges =
        checkIncomparableNodes g edges
        removeUnconstrainedEdges g edges
        g.TopologicalSort ()

    let addPrefConstraints idx cg cache doms (g: Constraints) mustPrefer nodes =
        let mutable edges = Set.empty
        for x in nodes do
            for y in nodes do
                if x <> y && (isPreferred idx cg cache doms (x,y)) then
                    logInfo (idx, sprintf "  %s is preferred to %s" (string x) (string y))
                    edges <- Set.add (x,y) edges
                    g.AddEdge (Edge(x, y)) |> ignore
                else if x <> y then
                    let b, ns = (mustPrefer: Dictionary<_,HashSet<_>>).TryGetValue(x)
                    if b && ns.Contains(y) then 
                        raise (SimplePathException (x,y))
                    logInfo (idx, sprintf "  %s is NOT preferred to %s" (string x) (string y))
        g, edges

    let encodeConstraints idx cache doms cg mustPrefer nodes =
        let g = BidirectionalGraph<CgState ,Edge<CgState>>()
        for n in nodes do 
            g.AddVertex n |> ignore
        addPrefConstraints idx cg cache doms g mustPrefer nodes

    let findPrefAssignment idx cache doms cg mustPrefer nodes = 
        let g, edges = encodeConstraints idx cache doms cg mustPrefer nodes
        getOrdering g edges

    let addForLabel idx cache doms ain cg mustPrefer (map: Dictionary<_,_>) l =
        if Set.contains l ain then
            if not (map.ContainsKey(l)) then 
                let nodes = Seq.filter (fun v -> loc v = l) cg.Graph.Vertices
                map.[l] <- findPrefAssignment idx cache doms cg mustPrefer nodes
        else map.[l] <- Seq.empty
        map


    let findOrdering idx cg : Result<Ordering, CounterExample> =
        try
            let mustPrefer = getHardPreferences cg
            let (ain, _) = Topology.alphabet cg.Topo
            let ain = Set.map (fun (v: Topology.Node) -> v.Loc) ain
            let doms = Domination.dominators cg cg.Start Down
            let cache = HashSet()
            let labels =
                cg.Graph.Vertices
                |> Seq.choose (fun v -> if Topology.isTopoNode v.Node then Some (loc v) else None)
                |> Set.ofSeq
            try Ok(Set.fold (addForLabel idx cache doms ain cg mustPrefer) (Dictionary()) labels)
            with ConsistencyException(x,y) -> Err((x,y) )
        with SimplePathException(x,y) -> Err(x,y)

    let findOrderingConservative (idx: int) =
        findOrdering idx


module ToRegex =

    let constructRegex (cg: T) (state: CgState) : Regex.T =
        let reMap = ref Map.empty
        let inline get v = Util.Map.getOrDefault v Regex.empty !reMap
        let inline add k v = reMap := Map.add k v !reMap
        let canReach = Reachable.dfs cg state Down
        cg.Graph.RemoveVertexIf (fun v -> not (canReach.Contains v) && Topology.isTopoNode v.Node) |> ignore
        cg.Graph.AddEdge (Edge(cg.End, state)) |> ignore
        add (cg.End, state) Regex.epsilon
        for e in cg.Graph.Edges do
            if e.Source <> cg.End then
                add (e.Source, e.Target) (Regex.loc (loc e.Source))
        let queue = Queue()
        for v in cg.Graph.Vertices do
            if isRealNode v then
                queue.Enqueue v 
        while queue.Count > 0 do 
            let q = queue.Dequeue()
            for q1 in cg.Graph.Vertices do 
                for q2 in cg.Graph.Vertices do
                    if q1 <> q && q2 <> q then
                        let x = get (q1,q2)
                        let y1 = get (q1,q)
                        let y2 = get (q,q)
                        let y3 = get (q,q2)
                        let re = Regex.union x (Regex.concatAll [y1; Regex.star y2; y3])
                        reMap := Map.add (q1,q2) re !reMap
            cg.Graph.RemoveVertex q |> ignore
        (!reMap).[(cg.End, cg.Start)]


module Failure =

    type FailType =
        | NodeFailure of Topology.Node
        | LinkFailure of Topology.Node * Topology.Node

        override x.ToString() = 
            match x with 
            | NodeFailure n -> "Node(" + n.Loc + ")"
            | LinkFailure (s,t) -> "Link(" + s.Loc + "," + t.Loc + ")"
  
    let allFailures n (topo: Topology.T) : seq<FailType list> =

        let fvs = Topology.vertices topo |> Seq.filter Topology.isInside |> Seq.map NodeFailure
        let fes =
            Topology.edges topo
            |> Seq.filter (fun (src,tgt) -> Topology.isInside src || Topology.isInside tgt) 
            |> Seq.map LinkFailure
        Seq.append fes fvs 
        |> Seq.toList
        |> Util.List.combinations n

    let failedGraph (cg: T) (failures: FailType list) : T =
        let failed = copyGraph cg
        let rec aux acc fs =
            let (vs,es) = acc 
            match fs with
            | [] -> acc
            | (NodeFailure s)::tl ->
                aux (s.Loc::vs, es) tl
            | (LinkFailure (s,t))::tl ->
                aux (vs, (s.Loc, t.Loc)::(t.Loc, s.Loc)::es) tl
        let (failedNodes, failedEdges) = aux ([],[]) failures
        failed.Graph.RemoveVertexIf (fun v -> 
            List.exists ((=) v.Node.Loc) failedNodes) |> ignore
        failed.Graph.RemoveEdgeIf (fun e -> 
            List.exists ((=) (loc e.Source, loc e.Target)) failedEdges) |> ignore
        failed

    let disconnect (cg: T) src dst : int =
        let cg = copyGraph cg
        let mutable removed = 0
        let mutable hasPath = true
        while hasPath do 
            let sp = cg.Graph.ShortestPathsDijkstra((fun _ -> 1.0), src)
            let mutable path = Seq.empty
            ignore (sp.Invoke(dst, &path))
            match path with 
            | null -> hasPath <- false
            | p ->
                removed <- removed + 1
                cg.Graph.RemoveEdgeIf (fun e -> 
                    Seq.exists ((=) e) p) |> ignore
        removed
       
    let disconnectAll (cg: T) srcs dsts =
        if Seq.isEmpty srcs || Seq.isEmpty dsts then None else
        let mutable smallest = System.Int32.MaxValue
        let mutable pair = None
        for src in srcs do 
            for dst in dsts do 
                let k = disconnect cg src dst
                if k < smallest then 
                    smallest <- k
                    pair <- Some (src,dst)
        let (x,y) = Option.get pair
        let k = max 0 (smallest - 1)
        Some (k, x.Node.Loc, y.Node.Loc)

    let disconnectLocs (cg: T) srcs dstLoc =
        let dsts = Seq.filter (fun v -> loc v = dstLoc) cg.Graph.Vertices 
        disconnectAll cg srcs dsts