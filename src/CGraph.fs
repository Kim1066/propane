﻿module CGraph

open Common.Error
open Common.Debug
open Common.Format
open System.Collections.Generic
open System.Diagnostics
open QuickGraph
open QuickGraph.Algorithms

[<CustomEquality; CustomComparison>]
type CgState =
    {Id: int;
     State: int;
     Accept: Set<int>; 
     Node: Topology.State;
     mutable Mark: uint32}

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
     TNode: Topology.State; 
     TAccept: Set<int>}

(*
[<Struct>]
type Edge = struct
    val X: int
    val Y: int
    new(x,y) = {X=x; Y=y}
end *)

type T = 
    {Start: CgState;
     End: CgState;
     Graph: BidirectionalGraph<CgState, Edge<CgState>>
     Topo: Topology.T
     mutable Mark: uint32}

type Direction = Up | Down

let copyGraph (cg: T) : T = 
    let newCG = QuickGraph.BidirectionalGraph()
    for v in cg.Graph.Vertices do 
        newCG.AddVertex v |> ignore
    for e in cg.Graph.Edges do
        newCG.AddEdge e |> ignore
    {Start=cg.Start; Graph=newCG; Mark=cg.Mark; End=cg.End; Topo=cg.Topo}

let copyReverseGraph (cg: T) : T = 
    let newCG = QuickGraph.BidirectionalGraph() 
    for v in cg.Graph.Vertices do newCG.AddVertex v |> ignore
    for e in cg.Graph.Edges do
        let e' = Edge(e.Target, e.Source)
        newCG.AddEdge e' |> ignore
    {Start=cg.Start; Graph=newCG; Mark=cg.Mark; End=cg.End; Topo=cg.Topo}

let stateMapper () = 
    let stateMap = Dictionary(HashIdentity.Structural)
    let counter = ref 0
    (fun ss -> 
        let mutable value = 0
        if stateMap.TryGetValue(ss, &value) then value
        else incr counter; stateMap.[ss] <- !counter; !counter)

let index ((graph, topo, startNode, endNode): BidirectionalGraph<CgStateTmp, Edge<CgStateTmp>> * _ * _ * _) =
    let newCG = QuickGraph.BidirectionalGraph()
    let mapper = stateMapper ()
    let nstart = {Id=0; Node=startNode.TNode; Mark=0u; State=(mapper startNode.TStates); Accept=startNode.TAccept}
    let nend = {Id=1; Node=endNode.TNode; Mark=0u; State=(mapper endNode.TStates); Accept=endNode.TAccept}
    ignore (newCG.AddVertex nstart)
    ignore (newCG.AddVertex nend)
    let mutable i = 2
    let idxMap = Dictionary(HashIdentity.Structural)
    idxMap.[(nstart.Node.Loc, nstart.State)] <- nstart
    idxMap.[(nend.Node.Loc, nend.State)] <- nend
    for v in graph.Vertices do
        if Topology.isTopoNode v.TNode then
            let newv = {Id=i; Node=v.TNode; Mark=0u; State = (mapper v.TStates); Accept=v.TAccept}
            i <- i + 1
            idxMap.Add( (v.TNode.Loc, mapper v.TStates), newv)
            newCG.AddVertex newv |> ignore
    for e in graph.Edges do
        let v = e.Source 
        let u = e.Target
        let x = idxMap.[(v.TNode.Loc, mapper v.TStates)]
        let y = idxMap.[(u.TNode.Loc, mapper u.TStates)]
        newCG.AddEdge (Edge(x,y)) |> ignore
    {Start=nstart; Graph=newCG; Mark=1u; End=nend; Topo=topo}

[<Struct>]
type KeyPair = struct
    val Q: int
    val C: string
    new(q,c) = {Q = q; C = c}
end

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
        if c = 1 && Set.minElement v = k then Some k 
        else None 
    let selfLoops = 
        Map.fold (fun acc (x,_) y  ->
            let existing = Common.Map.getOrDefault x Set.empty acc
            Map.add x (Set.add y existing) acc ) Map.empty auto.trans
            |> Seq.choose aux
            |> Set.ofSeq
    Set.difference selfLoops auto.F

let buildFromAutomata (topo: Topology.T) (autos : Regex.Automaton array) : T =
    if not (Topology.isWellFormed topo) then
        error (sprintf "Invalid topology. Topology must be weakly connected.")
    let unqTopo = Set.ofSeq topo.Vertices
    let transitions = getTransitions autos
    let garbage = Array.map getGarbageStates autos
    let graph = BidirectionalGraph<CgStateTmp, Edge<CgStateTmp>>()
    let starting = Array.map (fun (x: Regex.Automaton) -> x.q0) autos
    let newStart = {TStates = starting; TAccept = Set.empty; TNode = {Loc="start"; Typ = Topology.Start} }
    ignore (graph.AddVertex newStart)
    let marked = HashSet(HashIdentity.Structural)
    let todo = Queue()
    todo.Enqueue newStart
    while todo.Count > 0 do
        let currState = todo.Dequeue()
        let t = currState.TNode
        let adj = 
            if t.Typ = Topology.Start 
            then Seq.filter Topology.canOriginateTraffic unqTopo 
            else topo.OutEdges t |> Seq.map (fun e -> e.Target)
        let adj = if t.Typ = Topology.Unknown then Seq.append (Seq.singleton t) adj else adj
        for c in adj do
            let dead = ref true
            let nextInfo = Array.init autos.Length (fun i ->
                let g, v = autos.[i], currState.TStates.[i]
                let newState = transitions.[i].[(v,c.Loc)]
                if not (garbage.[i].Contains newState) then 
                    dead := false
                let accept =
                    if (Topology.canOriginateTraffic c) && (Set.contains newState g.F) 
                    then Set.singleton (i+1)
                    else Set.empty
                newState, accept)
            let nextStates, nextAccept = Array.unzip nextInfo
            let accept = Array.fold Set.union Set.empty nextAccept
            let state = {TStates=nextStates; TAccept=accept; TNode=c}
            if not !dead then
                if not (marked.Contains state) then
                    ignore (marked.Add state)
                    ignore (graph.AddVertex state)
                    todo.Enqueue state 
                let edge = Edge(currState, state)
                ignore (graph.AddEdge edge)
    let newEnd = {TStates = [||]; TAccept = Set.empty; TNode = {Loc="end"; Typ = Topology.End}}
    graph.AddVertex newEnd |> ignore
    for v in graph.Vertices do
        if not (Set.isEmpty v.TAccept) then
            let e = Edge(v, newEnd)
            ignore (graph.AddEdge(e))
    index (graph, topo, newStart, newEnd)

let inline loc x = x.Node.Loc

let inline shadows x y = 
    (x <> y) && (loc x = loc y)

let inline preferences (cg: T) : Set<int> = 
    let mutable all = Set.empty
    for v in cg.Graph.Vertices do 
        all <- Set.union all v.Accept
    all

let inline acceptingStates (cg: T) : Set<CgState> =
    cg.Graph.Vertices
    |> Seq.filter (fun (v: CgState) -> not v.Accept.IsEmpty)
    |> Set.ofSeq

let inline acceptingLocations (cg: T) : Set<string> = 
    acceptingStates cg
    |> Set.map loc

let inline isRealNode (state: CgState) : bool =
    Topology.isTopoNode state.Node

let inline neighbors (cg: T) (state: CgState) =
    seq {for e in cg.Graph.OutEdges state do yield e.Target}

let inline neighborsIn (cg: T) (state: CgState) =
    seq {for e in cg.Graph.InEdges state do yield e.Source}

let inline isRepeatedOut (cg: T) (state: CgState) =
    let ns = neighbors cg state
    (state.Node.Typ = Topology.Unknown) &&
    (Seq.exists ((=) state) ns)

let inline isInside x = 
    Topology.isInside x.Node

let inline isOutside x = 
    Topology.isOutside x.Node

let inline isEmpty (cg: T) = 
    cg.Graph.VertexCount = 2

let restrict (cg: T) (i: int) = 
    if Set.contains i (preferences cg) then 
        let copy = copyGraph cg
        copy.Graph.RemoveVertexIf (fun v -> 
            not (v.Accept.IsEmpty) && 
            not (Set.exists (fun i' -> i' <= i) v.Accept)) |> ignore
        copy
    else cg

let toDot (cg: T) : string = 
    let onFormatEdge(e: Graphviz.FormatEdgeEventArgs<CgState, Edge<CgState>>) = ()
    let onFormatVertex(v: Graphviz.FormatVertexEventArgs<CgState>) = 
        let states = string v.Vertex.State
        let location = loc v.Vertex
        match v.Vertex.Node.Typ with 
        | Topology.Start -> v.VertexFormatter.Label <- "Start"
        | Topology.End -> v.VertexFormatter.Label <- "End"
        | _ ->
            if Set.isEmpty v.Vertex.Accept then 
                v.VertexFormatter.Label <- states + ", " + location
            else
                v.VertexFormatter.Label <- states + ", " + location + "\nAccept=" + (Common.Set.toString v.Vertex.Accept)
                v.VertexFormatter.Shape <- Graphviz.Dot.GraphvizVertexShape.DoubleCircle
                v.VertexFormatter.Style <- Graphviz.Dot.GraphvizVertexStyle.Filled
                v.VertexFormatter.FillColor <- Graphviz.Dot.GraphvizColor.LightYellow
    let graphviz = Graphviz.GraphvizAlgorithm<CgState, Edge<CgState>>(cg.Graph)
    graphviz.FormatEdge.Add(onFormatEdge)
    graphviz.FormatVertex.Add(onFormatVertex)
    graphviz.Generate()

let generatePNG (cg: T) (file: string) : unit =
    System.IO.File.WriteAllText(file + ".dot", toDot cg)
    let p = new Process()
    p.StartInfo.FileName <- "dot"
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.Arguments <- "-Tpng " + file + ".dot -o " + file + ".png" 
    p.StartInfo.CreateNoWindow <- true
    p.Start() |> ignore
    p.WaitForExit();


module Reachable =

    let inline isMarked (cg:T) (v:CgState) =
        v.Mark = cg.Mark

    let inline mark (cg:T) (v: CgState) = 
        v.Mark <- cg.Mark

    let inline resetMarks (cg:T) =
        cg.Mark <- cg.Mark + 1u    

    let dfs (cg: T) (source: CgState) direction : seq<CgState> = 
        seq { 
            resetMarks cg
            let f = if direction = Up then neighborsIn else neighbors
            let s = Stack()
            s.Push source
            while s.Count > 0 do 
                let v = s.Pop()
                if not (isMarked cg v) then 
                    mark cg v
                    yield v
                    for w in f cg v do 
                        s.Push w}

    let srcWithout (cg: T) source without direction =
        let f = if direction = Up then neighborsIn else neighbors
        let s = Stack()
        let marked = HashSet(HashIdentity.Structural)
        s.Push source
        while s.Count > 0 do 
            let v = s.Pop()
            if not (marked.Contains v) && not (without v) then 
                ignore (marked.Add v)
                for w in f cg v do 
                    s.Push w
        marked

    let inline srcDstWithout (cg: T) source sink without direction = 
        if without sink || without source then false
        else 
            let marked = srcWithout cg source without direction
            marked.Contains(sink)

    let inline src (cg: T) (source: CgState) direction : HashSet<CgState> =
        srcWithout cg source (fun _ -> false) direction

    let inline srcDst (cg: T) source sink direction = 
        srcDstWithout cg source sink (fun _ -> false) direction

    let inline srcAcceptingWithout cg src without direction = 
        let marked = srcWithout cg src without direction 
        let mutable all = Set.empty 
        for cg in marked do 
            all <- Set.union cg.Accept all
        all

    let inline srcAccepting cg src direction = 
        srcAcceptingWithout cg src (fun _ -> false) direction


module Domination = 

    type DominationSet = Dictionary<CgState, Set<CgState>>
    type DominationTree = Dictionary<CgState, CgState option>

    let dominatorSet (dom: DominationTree) x = 
        let mutable ds = Set.singleton x
        match dom.[x] with 
        | None -> ds
        | Some v -> 
            let mutable runner = x
            let mutable current = v
            while runner <> current do 
                ds <- Set.add runner ds
                runner <- current
                current <- Common.Option.get dom.[runner]
            ds

    let inter (po: Dictionary<CgState,int>) (dom: DominationTree) b1 b2 =
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
        
    let dominators (cg: T) root direction : DominationSet =
        let adj = if direction = Up then neighbors cg else neighborsIn cg
        let dom = Dictionary()
        let reach = Reachable.dfs cg root direction
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
                    if Option.isNone x || Option.get x <> newIDom then
                        dom.[b] <- Some newIDom
                        changed <- true
        let res = Dictionary()
        for v in cg.Graph.Vertices do 
            res.[v] <- dominatorSet dom v
        res


module Minimize =

    let removeDominated (cg: T) = 
        let dom = Domination.dominators cg cg.Start Down
        let domRev = Domination.dominators cg cg.End Up
        cg.Graph.RemoveVertexIf (fun v ->
            (not (isRepeatedOut cg v)) &&
            Set.union dom.[v] domRev.[v] |> Set.exists (shadows v)) |> ignore
        cg.Graph.RemoveEdgeIf (fun e -> 
            let ies = cg.Graph.OutEdges e.Target
            match Seq.tryFind (fun (ie: Edge<CgState>) -> ie.Target = e.Source) ies with 
            | None -> false 
            | Some ie ->
                assert (ie.Source = e.Target)
                assert (ie.Target = e.Source)
                (not (isRepeatedOut cg e.Source || isRepeatedOut cg e.Target)) &&
                (Set.contains e.Target (dom.[e.Source]) || Set.contains e.Source (domRev.[e.Target])) &&
                (e.Target <> e.Source) ) |> ignore
        cg.Graph.RemoveEdgeIf (fun e ->
            let x = e.Source
            let y = e.Target
            (not (isRepeatedOut cg e.Source || isRepeatedOut cg e.Target)) &&
            (Set.exists (shadows x) domRev.[y] || 
             Set.exists (shadows y) dom.[x])) |> ignore
        cg

    let removeNodesThatCantReachEnd (cg: T) = 
        let canReach = Reachable.src cg cg.End Up
        cg.Graph.RemoveVertexIf(fun v -> 
            Topology.isTopoNode v.Node && not (canReach.Contains(v))) |> ignore
        cg
        
    let removeNodesThatStartCantReach (cg: T) = 
        let canReach = Reachable.src cg cg.Start Down
        cg.Graph.RemoveVertexIf(fun v -> 
            Topology.isTopoNode v.Node && not (canReach.Contains(v))) |> ignore
        cg

    let delMissingSuffixPaths cg = 
        let starting = neighbors cg cg.Start |> Seq.filter isRealNode |> Set.ofSeq
        cg.Graph.RemoveVertexIf (fun v -> 
            v.Node.Typ = Topology.InsideOriginates && 
            v.Accept.IsEmpty && 
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
        cg

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
        cg 
          
    let minimize (idx: int) (cg: T) =
        logInfo(idx, sprintf "Node count: %d" cg.Graph.VertexCount)
        let inline count cg = 
            cg.Graph.VertexCount + cg.Graph.EdgeCount
        let cg = ref cg
        let inline prune () = 
            cg := removeNodesThatCantReachEnd !cg
            logInfo(idx, sprintf "Node count (cant reach end): %d" (!cg).Graph.VertexCount)
            cg := removeDominated !cg
            logInfo(idx, sprintf "Node count (remove dominated): %d" (!cg).Graph.VertexCount)
            cg := removeRedundantExternalNodes !cg
            logInfo(idx, sprintf "Node count (redundant external nodes): %d" (!cg).Graph.VertexCount)
            cg := removeConnectionsToOutStar !cg
            logInfo(idx, sprintf "Node count (connections to out*): %d" (!cg).Graph.VertexCount)
            cg := removeNodesThatStartCantReach !cg
            logInfo(idx, sprintf "Node count (start cant reach): %d" (!cg).Graph.VertexCount)
        let mutable sum = count !cg
        prune() 
        while count !cg <> sum do
            sum <- count !cg
            prune ()
        logInfo(idx, sprintf "Node count - after O3: %d" (!cg).Graph.VertexCount)
        !cg


module Consistency = 

    exception SimplePathException of CgState * CgState
    exception ConsistencyException of CgState * CgState

    type CounterExample =  CgState * CgState
    type Preferences = seq<CgState>
    type Ordering = Map<string, Preferences>
    type Constraints = BidirectionalGraph<CgState ,Edge<CgState>>

    type Node = struct 
        val More: CgState
        val Less: CgState
        new(m, l) = {More=m; Less=l}
    end

    type ProtectResult =
        | Yes of HashSet<Node>
        | No

    let protect (idx: int) (doms: Domination.DominationSet) (cg1,n1) (cg2,n2) : ProtectResult = 
        if loc n1 <> loc n2 then No else
        let q = Queue()
        let seen = HashSet()
        let init = Node(n1,n2)
        q.Enqueue init
        ignore(seen.Add init)
        let inline add x' y' = 
            let n' = Node(x',y')
            if not (seen.Contains n') then 
                ignore (seen.Add n')
                q.Enqueue n'
        let mutable counterEx = None
        while q.Count > 0 && Option.isNone counterEx do
            let n = q.Dequeue()
            let x = n.More 
            let y = n.Less 
            let nsx = 
                neighbors cg1 x 
                |> Seq.fold (fun acc x -> Map.add (loc x) x acc) Map.empty
            let nsy = neighbors cg2 y
            for y' in nsy do 
                match Map.tryFind (loc y') nsx with
                | None ->
                    match Seq.tryFind (fun x' -> loc x' = loc y' && cg1.Graph.ContainsVertex x') doms.[x] with
                    | None -> counterEx <- Some (x,y)
                    | Some x' ->  add x' y'
                | Some x' -> add x' y'
        match counterEx with 
        | None -> Yes seen
        | Some cex -> No

    let getDuplicateNodes (cg: T) = 
        cg.Graph.Vertices
        |> Seq.fold (fun acc v ->
                let existing = Common.Map.getOrDefault (loc v) Set.empty acc
                Map.add (loc v) (Set.add v existing) acc) Map.empty
        |> Map.filter (fun k v -> Set.count v > 1)

    let allDisjoint (cg: T) dups = 
        let components = Dictionary() :> IDictionary<CgState,int>
        cg.Graph.WeaklyConnectedComponents(components) |> ignore
        Map.forall (fun k v -> 
            let szInit = Set.count v
            let szFinal = Set.map (fun x -> components.[x]) v |> Set.count
            szInit = szFinal) dups

    let getHardPreferences (cg: T) = 
        let cg = copyGraph cg
        cg.Graph.RemoveVertexIf (fun v -> isOutside v || not (isRealNode v)) |> ignore
        let dups = getDuplicateNodes cg
        let size = Map.fold (fun acc _ _ -> acc + 1) 0 dups
        if size = 0 || allDisjoint cg dups then
            Map.empty 
        else 
            let dups = Map.fold (fun acc _ v -> Set.union acc v) Set.empty dups
            let mutable mustPrefer = Map.empty
            for d in dups do 
                let reach = Reachable.src cg d Down
                let below = Seq.filter (shadows d) reach
                if not (Seq.isEmpty below) then
                    mustPrefer <- Map.add d below mustPrefer
            mustPrefer

    let simulate idx cg cache (doms: Domination.DominationSet) restrict (x,y) (i,j) =
        if Set.contains (x,y,i,j) !cache then true else
        let restrict_i = Map.find i restrict
        let restrict_j = Map.find j restrict
        if not (restrict_i.Graph.ContainsVertex x) then false
        else if not (restrict_j.Graph.ContainsVertex y) then true
        else
            match protect idx doms (restrict_i, x) (restrict_j, y) with 
            | No -> false
            | Yes related -> 
                for n in related do 
                    cache := Set.add (n.More, n.Less, i, j) !cache
                true

    let isPreferred idx cg cache doms restrict (x,y) (reachX, reachY) =
        let subsumes i j =
            simulate idx cg cache doms restrict (x,y) (i,j)
        Set.forall (fun j -> 
            (Set.exists (fun i' -> i' <= j && subsumes i' j) reachX) ) reachY

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
    
    let getReachabilityMap (cg:T) =
        let prefs = preferences cg
        let getNodesWithPref acc i = 
            let copy = copyGraph cg
            copy.Graph.RemoveEdgeIf (fun e ->
                e.Target = copy.End && not (e.Source.Accept.Contains i)) |> ignore
            let reach = Reachable.src copy copy.End Up
            Seq.fold (fun acc v ->
                let existing = Common.Map.getOrDefault v Set.empty acc 
                let updated = Map.add v (Set.add i existing) acc
                updated) acc reach //baddddd
        Set.fold getNodesWithPref Map.empty prefs    

    let addPrefConstraints idx cg cache doms (g: Constraints) r mustPrefer nodes reachMap =
        let mutable edges = Set.empty
        for x in nodes do
            for y in nodes do
                let reachX = Map.find x reachMap
                let reachY = Map.find y reachMap
                if x <> y && (isPreferred idx cg cache doms r (x,y) (reachX,reachY)) then
                    logInfo (idx, sprintf "  %s is preferred to %s" (string x) (string y))
                    edges <- Set.add (x,y) edges
                    g.AddEdge (Edge(x, y)) |> ignore
                else if x <> y then
                    match Map.tryFind x mustPrefer with 
                    | None -> ()
                    | Some ns ->
                        if Seq.contains y ns then 
                            raise (SimplePathException (x,y))
                    logInfo (idx, sprintf "  %s is NOT preferred to %s" (string x) (string y))
        g, edges

    let encodeConstraints idx cache doms (cg, reachMap) mustPrefer r nodes =
        let g = BidirectionalGraph<CgState ,Edge<CgState>>()
        for n in nodes do 
            g.AddVertex n |> ignore
        addPrefConstraints idx cg cache doms g r mustPrefer nodes reachMap

    let findPrefAssignment idx cache doms r (cg, reachMap) mustPrefer nodes = 
        let g, edges = encodeConstraints idx cache doms (cg, reachMap) mustPrefer r nodes
        getOrdering g edges
        
    let addForLabel idx cache doms ain r (cg, reachMap) mustPrefer map l =
        if Set.contains l ain then
            if not (Map.containsKey l map) then 
                let nodes = Seq.filter (fun v -> loc v = l) cg.Graph.Vertices
                Map.add l (findPrefAssignment idx cache doms r (cg, reachMap) mustPrefer nodes) map
            else map
        else Map.add l Seq.empty map

    let restrictedGraphs cg prefs =
        let aux acc i =
            let r = restrict cg i 
            let r = Minimize.removeNodesThatCantReachEnd r
            r.Graph.RemoveEdgeIf (fun e -> isOutside e.Source && isOutside e.Target) |> ignore
            r.Graph.RemoveEdgeIf (fun e -> not (isRealNode e.Source) || not (isRealNode e.Target) ) |> ignore
            Map.add i r acc
        Set.fold aux Map.empty prefs

    let findOrdering idx cg outName : Result<Ordering, CounterExample> =
        try 
            let mustPrefer = getHardPreferences cg
            let prefs = preferences cg 
            let rs = restrictedGraphs cg prefs
            let reachMap = getReachabilityMap cg
            let (ain, _) = Topology.alphabet cg.Topo
            let ain = Set.map (fun (v: Topology.State) -> v.Loc) ain
            let doms = Domination.dominators cg cg.Start Down
            let cache = ref Set.empty
            debug (fun () -> Map.iter (fun i g -> generatePNG g (outName + "-min-restricted" + string i)) rs)
            let labels =
                cg.Graph.Vertices
                |> Seq.choose (fun v -> if Topology.isTopoNode v.Node then Some (loc v) else None)
                |> Set.ofSeq
            try Ok(Set.fold (addForLabel idx cache doms ain rs (cg, reachMap) mustPrefer) Map.empty labels)
            with ConsistencyException(x,y) -> Err((x,y) )
        with SimplePathException(x,y) -> Err(x,y)

    let findOrderingConservative (idx: int) = findOrdering idx


module ToRegex =

    let constructRegex (cg: T) (state: CgState) : Regex.T =
        let reMap = ref Map.empty
        let inline get v = Common.Map.getOrDefault v Regex.empty !reMap
        let inline add k v = reMap := Map.add k v !reMap
        let reachable = Reachable.src cg state Down
        cg.Graph.RemoveVertexIf (fun v -> not (reachable.Contains v) && Topology.isTopoNode v.Node) |> ignore
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
        | NodeFailure of Topology.State
        | LinkFailure of Edge<Topology.State>

        override x.ToString() = 
            match x with 
            | NodeFailure n -> "Node(" + n.Loc + ")"
            | LinkFailure e -> "Link(" + e.Source.Loc + "," + e.Target.Loc + ")"
  
    let allFailures n (topo: Topology.T) : seq<FailType list> =
        let fvs = topo.Vertices |> Seq.filter Topology.isInside |> Seq.map NodeFailure
        let fes =
            topo.Edges
            |> Seq.filter (fun e -> Topology.isInside e.Source || Topology.isInside e.Target) 
            |> Seq.map LinkFailure
        Seq.append fes fvs 
        |> Seq.toList
        |> Common.List.combinations n

    let failedGraph (cg: T) (failures: FailType list) : T =
        let failed = copyGraph cg
        let rec aux acc fs =
            let (vs,es) = acc 
            match fs with
            | [] -> acc
            | (NodeFailure s)::tl ->
                aux (s.Loc::vs, es) tl
            | (LinkFailure s)::tl ->
                aux (vs, (s.Source.Loc, s.Target.Loc)::(s.Target.Loc, s.Source.Loc)::es) tl
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