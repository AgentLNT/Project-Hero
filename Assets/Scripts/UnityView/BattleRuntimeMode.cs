using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    // Historical numeric values are retained solely to reject old serialized configuration.
    public enum BattleRuntimeMode
    {
        Legacy = 0,
        Shadow = 1,
        New = 2
    }
}
