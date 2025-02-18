﻿using System;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.GeneratedSheets;

/* NOTE (Chiv) From
 * https://github.com/Caraxi/RemindMe/blob/8f8a353f4696d4b64e218964a3ad47841a24a706/Extensions.cs
 * under AGLPv3 license 
*/
namespace GentleTouch.Interop
{

    public static class Extensions
    {
        public static bool IsStatus(this PlayerCharacter actor, StatusFlags flags)
        {
            return (actor.StatusFlags & flags) > 0;
        }

        public static bool HasClass(this ClassJobCategory cjc, uint classJobRowId)
        {
            return classJobRowId switch
            {
                0 => cjc.ADV,
                1 => cjc.GLA,
                2 => cjc.PGL,
                3 => cjc.MRD,
                4 => cjc.LNC,
                5 => cjc.ARC,
                6 => cjc.CNJ,
                7 => cjc.THM,
                8 => cjc.CRP,
                9 => cjc.BSM,
                10 => cjc.ARM,
                11 => cjc.GSM,
                12 => cjc.LTW,
                13 => cjc.WVR,
                14 => cjc.ALC,
                15 => cjc.CUL,
                16 => cjc.MIN,
                17 => cjc.BTN,
                18 => cjc.FSH,
                19 => cjc.PLD,
                20 => cjc.MNK,
                21 => cjc.WAR,
                22 => cjc.DRG,
                23 => cjc.BRD,
                24 => cjc.WHM,
                25 => cjc.BLM,
                26 => cjc.ACN,
                27 => cjc.SMN,
                28 => cjc.SCH,
                29 => cjc.ROG,
                30 => cjc.NIN,
                31 => cjc.MCH,
                32 => cjc.DRK,
                33 => cjc.AST,
                34 => cjc.SAM,
                35 => cjc.RDM,
                36 => cjc.BLU,
                37 => cjc.GNB,
                38 => cjc.DNC,
                39 => cjc.RPR,
                40 => cjc.SGE,
                _ => false
            };
        }
    }
}