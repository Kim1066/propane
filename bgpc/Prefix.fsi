﻿module Prefix

/// A 32-bit prefix
type T =
    {X1: uint32; 
     X2: uint32; 
     X3: uint32; 
     X4: uint32; 
     Slash: uint32}

/// Range-based representation of a prefix
/// Ranges are kept sorted and overlapping
/// ranges are merged whenever possible
type Pred = Pred of (uint32 * uint32) list

/// Constructor for a prefix
val prefix: (uint32*uint32*uint32*uint32) -> uint32 -> T

val str: Pred -> string

val fromRange: uint32 * uint32 -> Pred

/// Logical disjunction of two prefixes
val disj: Pred -> Pred -> Pred

/// Logical conjunction of two prefixes
val conj: Pred -> Pred -> Pred

/// Logical false
val bot: Pred

/// Logical true
val top: Pred

/// Logical Negation of a prefix
val negation: Pred -> Pred

/// Convert list of prefixes to a range-based representation
val toPredicate: T list -> Pred

/// Convert a range-based representation to a list of prefixes
val toPrefixes: Pred -> T list

