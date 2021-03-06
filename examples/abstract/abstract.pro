define Peer = {Peer1, Peer2}

define transit(X,Y) = enter(X) and exit(Y)

define notransit = {
	true => not transit(Peer, Peer)
}

define routing = {
	T0.$prefix$ => end(T0.$router$),
	true => exit(Peer1) >> exit(Peer2)
}

define main = routing and notransit

control {
	aggregate($aggregatePrefix$, in -> out),
	tag(8075:1, 0.0.0.0/0, in -> out),
	maxroutes(10, T0 -> T1),
	longest_path(10, T0)
}