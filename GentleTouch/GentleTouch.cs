﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Game.Command;
using Dalamud.Game.Internal;
using Dalamud.Hooking;
using GentleTouch.Caraxi;
using GentleTouch.Collection;
using ImGuiNET;
using static System.Int32;
using FFXIVAction = Lumina.Excel.GeneratedSheets.Action;

namespace GentleTouch
{
    public class GentleTouch : IDisposable
    {
        private const string Command = "/gentle";

        // TODO (Chiv): Check Right and Left Motor for x360/XOne Gamepad
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void FFXIVSetState(nint maybeControllerStruct, int rightMotorSpeedPercent, int leftMotorSpeedPercent);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int XInputWrapperSetState(int dwUserIndex, ref XInputVibration pVibration);
        private delegate int MaybeControllerPoll(nint maybeControllerStruct);
        // NOTE (Chiv) modified from
        // https://github.com/Caraxi/SimpleTweaksPlugin/blob/078c48947fce3578d631cd2de50245005aba8fdd/GameStructs/ActionManager.cs
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ref Cooldown GetActionCooldownSlot(nint actionManager, int cooldownGroup);
        
        private readonly FFXIVSetState _ffxivSetState;
        private readonly XInputWrapperSetState _xInputWrapperSetState;
        private readonly Hook<MaybeControllerPoll> _controllerPoll;
        private readonly nint _actionManager;
        private readonly GetActionCooldownSlot _getActionCooldownSlot;
        private readonly DalamudPluginInterface _pluginInterface;
        private Configuration _config;

        #if DEBUG
        // TODO TESTING
        
        private int _rightMotorSpeed = 100;
        private int _leftMotorSpeed = 0;
        private int _dwControllerIndex = 1;
        private int _cooldownGroup = 58;
        private int _nextStep = 0;
        private long _nextTimeStep = 0;
        private int[] _lastReturnedFromPoll = new int[100];
        private int _currentIndex = 0;

        private bool reset = false;
        private VibrationPattern _pattern = new()
        {
            Steps = new[]
            {
                new VibrationPattern.Step(50, 50, 200),
                new VibrationPattern.Step(0, 0, 200),
            },
            //Cycles = int.MaxValue,
            Infinite = true,
            Name = "SimplePattern"
        };

        private readonly List<VibrationCooldownTrigger> _debugTriggers = new();
        private static readonly IComparer<int> Comparer = Comparer<int>.Create((lhs, rhs) => rhs - lhs);
        private readonly PriorityQueue<VibrationCooldownTrigger, int> _queue= new ();

        private readonly SortedDictionary<int, VibrationCooldownTrigger> _dicQueue = new(Comparer);

        private IEnumerator<VibrationPattern.Step?>? _currentEnumerator;

        private VibrationCooldownTrigger? _highestPriorityTrigger;
        //TODO END TESTING
        #endif

        private nint _maybeControllerStruct;
        private bool _shouldDrawConfigUi =
#if DEBUG
                true
#else
            false
#endif
            ;

        private bool _isDisposed;


        public GentleTouch(DalamudPluginInterface pi, Configuration config)
        {
            #region Signatures
            const string maybeControllerPollSignature =
                "40 ?? 57 41 ?? 48 81 EC ?? ?? ?? ?? 44 0F ?? ?? ?? ?? ?? ?? ?? 48 8B";
            // TODO (chiv): '??'s at signatures end can be omitted, can't they?
            const string maybeControllerPollSignatureAlternative =
                "40 56 57 41 56 48 81 EC ?? ?? ?? ?? 44 0F 29 84 24 ?? ?? ?? ??";
            const string xInputWrapperSetStateSignature =
                "48 ff 25 69 28 ce 01 cc cc cc cc cc cc cc cc cc 48 89 5c 24";
            const string xInputWrapperSetStateSignatureAlternative = 
                "48 FF ?? ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC 48 89 ?? ?? ?? 48 89 ?? ?? ?? 48 89 ?? ?? ?? 48 89";
            const string ffxivSetStateSignature =
                "40 55 53 56 48 8b ec 48 81 ec 80 00 00 00 33 f6 44 8b d2 4c 8b c9";
            const string ffxivSetStateSignatureAlternative =
                "40 ?? 53 56 48 8B ?? 48 81 EC";
            //NOTE (Chiv): Signature from :
            // https://github.com/Caraxi/SimpleTweaksPlugin/blob/078c48947fce3578d631cd2de50245005aba8fdd/Helper/Common.cs
            const string actionManagerSignature = "E8 ?? ?? ?? ?? 33 C0 E9 ?? ?? ?? ?? 8B 7D 0C";
            //NOTE (Chiv): Signature from :
            // https://github.com/Caraxi/SimpleTweaksPlugin/blob/078c48947fce3578d631cd2de50245005aba8fdd/GameStructs/ActionManager.cs
            const string getActionCooldownSlotSignature = "E8 ?? ?? ?? ?? 0F 57 FF 48 85 C0";
            #endregion
            
            // TODO TESTING
            //config.Patterns.RemoveAll(p => true);
            if (config.Patterns.Count == 0)
            {
             
                config.Patterns.Add(_pattern);
                pi.SavePluginConfig(config);
            }

            PluginLog.Warning($"{config.Patterns[1].Name}");
            _debugTriggers.Add(new VibrationCooldownTrigger("GCD", -1, 58, 2, config.Patterns[1]));
            _debugTriggers.Add(new VibrationCooldownTrigger("Hide", 2245, 6, 1, config.Patterns[0]));
            _debugTriggers.Add(new VibrationCooldownTrigger("Shade Shift", 2241, 21, 0, config.Patterns[0]));

            _queue.EnqueueRange(_debugTriggers.Select(t => (t, t.Priority)));
            var first = _queue.Dequeue();
            var second = _queue.Dequeue();
            var third = _queue.Dequeue();
            PluginLog.Log($"Name {first.ActionName}, Prio: {first.Priority}");
            PluginLog.Log($"Name {second.ActionName}, Prio: {second.Priority}");
            PluginLog.Log($"Name {third.ActionName}, Prio: {third.Priority}");
            PluginLog.Log($"Empty? {_queue.Count == 0}");
            // TODO END TESTING
            
            _pluginInterface = pi;
            _config = config;
            _pluginInterface.UiBuilder.OnOpenConfigUi += OnOpenConfigUi;
            _pluginInterface.UiBuilder.OnBuildUi += BuildUi;
            _pluginInterface.Framework.OnUpdateEvent += FrameworkOutOfCombatUpdate;

            #region Hooks, Functions and Addresses

            _ffxivSetState = Marshal.GetDelegateForFunctionPointer<FFXIVSetState>(
                _pluginInterface.TargetModuleScanner.ScanText(ffxivSetStateSignature));
            _xInputWrapperSetState = Marshal.GetDelegateForFunctionPointer<XInputWrapperSetState>(
                _pluginInterface.TargetModuleScanner.ScanText(xInputWrapperSetStateSignature));
            _controllerPoll = new Hook<MaybeControllerPoll>(
                _pluginInterface.TargetModuleScanner.ScanText(maybeControllerPollSignature),
                (MaybeControllerPoll) ControllerPollDetour );
            _controllerPoll.Enable();
            _actionManager =
                _pluginInterface.TargetModuleScanner.GetStaticAddressFromSig(actionManagerSignature);
            _getActionCooldownSlot = Marshal.GetDelegateForFunctionPointer<GetActionCooldownSlot>(
                _pluginInterface.TargetModuleScanner.ScanText(getActionCooldownSlotSignature));
            #endregion
            
            //TODO TESTING
            pi.CommandManager.AddHandler(Command, new CommandInfo((_, _) => { OnOpenConfigUi(null!, null!); })
            {
                HelpMessage = "Become gentle.",
                ShowInHelp = true
            });
            //TODO END TESTING
        }
        
        private void FrameworkInCombatUpdate(Framework framework)
        {
            void ToOutOfCombatLoop()
            {
                ControllerSetState(0, 0);
                _pluginInterface.Framework.OnUpdateEvent += FrameworkOutOfCombatUpdate;
                _pluginInterface.Framework.OnUpdateEvent -= FrameworkInCombatUpdate;
            }

            if (!this._pluginInterface.ClientState.LocalPlayer.IsStatus(StatusFlags.InCombat))
            {
                _queue.Clear();
                _highestPriorityTrigger = null;
                _currentEnumerator?.Dispose();
                _currentEnumerator = null;
                return;
            }
            var weaponOut = _config.OnlyVibrateWithDrawnWeapon && this._pluginInterface.ClientState.LocalPlayer.IsStatus(StatusFlags.WeaponOut);
            if (_config.OnlyVibrateWithDrawnWeapon && !weaponOut)
            {
                
                ToOutOfCombatLoop();
                return;
            }

            UpdateQueue();
            UpdateHighestPriorityTrigger();
            CheckAndVibrate();
        }

        private void CheckAndVibrate()
        {
            if (_currentEnumerator is null) return;
            if (_currentEnumerator.MoveNext())
            {
                var s = _currentEnumerator.Current;
                if (s is not null)
                {
                    ControllerSetState(s.LeftMotorPercentage, s.RightMotorPercentage);
                }
            }
            else
            {
                ControllerSetState(0, 0);
                _currentEnumerator.Dispose();
                _currentEnumerator = null;
                _highestPriorityTrigger = null;
            }
        }

        private void UpdateHighestPriorityTrigger()
        {
            if (!_queue.TryPeek(out _, out var priority)) return;
            PluginLog.Warning($"Heap element Prio: {priority}, currentElementPrio: {_highestPriorityTrigger?.Priority}");
            if ((_highestPriorityTrigger?.Priority ?? MaxValue) <= priority) return;
            if (_highestPriorityTrigger?.Pattern.Infinite ?? false)
            {
                _highestPriorityTrigger.ShouldBeTriggered = true;
            }
            _currentEnumerator?.Dispose();
            _highestPriorityTrigger = _queue.Dequeue();
            _currentEnumerator = _highestPriorityTrigger.Pattern.GetEnumerator();
            PluginLog.Warning($"{(_currentEnumerator is null ? "Enumerator is null!" : "Enumereator not null")}");
            ControllerSetState(0, 0);
            PluginLog.Warning($"Set Current to {_highestPriorityTrigger.ActionName}");
        }

        private void UpdateQueue()
        {
            var cooldowns =
                _debugTriggers.Select(t => (t, _getActionCooldownSlot(_actionManager, t.ActionCooldownGroup - 1)));


            var tuples = cooldowns as (VibrationCooldownTrigger t, Cooldown)[] ?? cooldowns.ToArray();
            foreach (var (t, _) in tuples.Where(t => t.Item2))
            {
                if (_highestPriorityTrigger == t)
                {
                    _currentEnumerator!.Dispose();
                    _currentEnumerator = null;
                    _highestPriorityTrigger = null;
                    ControllerSetState(0, 0);
                }

                t.ShouldBeTriggered = true;
            }

            var filtered = tuples.Where(t => !t.Item2 && t.t.ShouldBeTriggered);
            var valueTuples = filtered as (VibrationCooldownTrigger t, Cooldown)[] ?? filtered.ToArray();
            foreach (var valueTuple in valueTuples)
            {
                valueTuple.t.ShouldBeTriggered = false;
            }

            _queue.EnqueueRange(valueTuples.Select(t => (t.t, t.t.Priority)));
        }

        private void FrameworkOutOfCombatUpdate(Framework framework)
        {
            
            var inCombat = this._pluginInterface.ClientState.LocalPlayer.IsStatus(StatusFlags.InCombat);
            if (!inCombat) return;
            var weaponOut = _config.OnlyVibrateWithDrawnWeapon && this._pluginInterface.ClientState.LocalPlayer.IsStatus(StatusFlags.WeaponOut);
            if (_config.OnlyVibrateWithDrawnWeapon && !weaponOut) return;
            _pluginInterface.Framework.OnUpdateEvent += FrameworkInCombatUpdate;
            _pluginInterface.Framework.OnUpdateEvent -= FrameworkOutOfCombatUpdate;


        }
        
        private void ControllerSetState(int leftMotorPercentage, int rightMotorPercentage, bool direct = true, int dwControllerIndex = 1)
        {
            #if DEBUG
            PluginLog.Verbose($"Setting controller state to L: {leftMotorPercentage}, R: {rightMotorPercentage}, direct? {direct}, Index: {dwControllerIndex}");
            #endif
            if (direct)
            {
                var t = new XInputVibration((ushort)(leftMotorPercentage * ushort.MaxValue), (ushort)(rightMotorPercentage * ushort.MaxValue));
                _xInputWrapperSetState(dwControllerIndex, ref t);
            }
            else
            {
                _ffxivSetState(_maybeControllerStruct, rightMotorPercentage, leftMotorPercentage);
            }
        }

        private int ControllerPollDetour(nint maybeControllerStruct)
        {
            _maybeControllerStruct = maybeControllerStruct;
            #if DEBUG
            var original = _controllerPoll.Original(maybeControllerStruct);
            _lastReturnedFromPoll[_currentIndex++ % _lastReturnedFromPoll.Length] = original;
            // TODO (Chiv) Interpretation happens inside method, log appears after map (0x40 = Square/X)
            //if(original is 0x40) PluginLog.Warning("Should block!");
            //return original is 0x40 ? 0 : original;
            return original;
#else
            _controllerPoll.Disable();
            return _controllerPoll.Original(maybeControllerStruct);
#endif

        }

        #region UI

        private void BuildUi()
        {
            _shouldDrawConfigUi = _shouldDrawConfigUi &&
                                  ConfigurationUi.DrawConfigUi(ref _config, _pluginInterface.SavePluginConfig);
            
            #if DEBUG
            DrawDebugUi();
            #endif
        }
        #if DEBUG
        private void DrawDebugUi()
        {
            ImGui.Begin($"{Constant.PluginName} Debug");
            ImGui.Text($"Same Pattern ID? {(_config.Patterns[1] == _debugTriggers[0].Pattern)}");
            ImGui.Text($"Trigger: {_debugTriggers[0]?.ActionName}");
            foreach (var step in _debugTriggers[0]?.Pattern?.Steps)
            {
                ImGui.Text($"Step L: {step.LeftMotorPercentage} R: {step.RightMotorPercentage} {step.MillisecondsTillNextStep}ms");
            }
            ImGui.Separator();
            ImGui.Text($"{_maybeControllerStruct.ToString("X12")}:{nameof(_maybeControllerStruct)}");
            ImGui.Text($"{nameof(ControllerPollDetour)} return Array (hex): ");
            foreach (var i in _lastReturnedFromPoll)
            {
                ImGui.SameLine();
                ImGui.Text($"{i:X}");
            }
            ImGui.Text($"{Marshal.ReadInt32(_maybeControllerStruct,0x88):X}:int (hex) at {nameof(_maybeControllerStruct)}+0x88");
            ImGui.Separator();
            ImGui.Text("First Pattern Name: "); ImGui.SameLine(); ImGui.Text($"{_config.Patterns[0]?.Name}");
            
            ImGui.Separator();
            ImGui.PushItemWidth(100);
            ImGui.InputInt("RightMotorSpeed", ref _rightMotorSpeed);
            ImGui.SameLine(); ImGui.InputInt("LeftMotorSpeed", ref _leftMotorSpeed);
            ImGui.InputInt("Cooldown Group", ref _cooldownGroup);
            ImGui.SameLine(); ImGui.InputInt("Controller Index", ref _dwControllerIndex);
            if (ImGui.Button("FFXIV SetState"))
            {
                ControllerSetState((ushort)_leftMotorSpeed, (ushort)_rightMotorSpeed);
            }

            ImGui.SameLine(); if (ImGui.Button("XInput Wrapper SetSate"))
            {
                ControllerSetState((ushort)_leftMotorSpeed, (ushort)_rightMotorSpeed, true, _dwControllerIndex);
            }
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1,0,0,1));
            ImGui.SameLine(); if (ImGui.Button("Stop Vibration"))
            {
                ControllerSetState(0,0,true,0);
                ControllerSetState(0,0,true,1);
            }
            ImGui.PopStyleColor();
            ImGui.Separator();
            ImGui.PopItemWidth();
            // NOTE (Chiv): 58 is GCD
            var cooldown = _getActionCooldownSlot(_actionManager, _cooldownGroup - 1);
            ImGui.Text($"Cooldown Elapsed: {cooldown.CooldownElapsed}");
            ImGui.Text($"Cooldown Total: {cooldown.CooldownTotal}");
            ImGui.Text($"IsCooldown: {cooldown.IsCooldown}");
            ImGui.Text($"ActionID: {cooldown.ActionID}");
            ImGui.End();
        }
        #endif
        

        private void OnOpenConfigUi(object sender, EventArgs e)
        {
            _shouldDrawConfigUi = !_shouldDrawConfigUi;
        }
        
        #endregion

        #region Dispose

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            PluginLog.Warning($"Disposing: {disposing}");
            if (_isDisposed) return;
            if (disposing)
            {
                _pluginInterface.UiBuilder.OnOpenConfigUi -= OnOpenConfigUi;
                _pluginInterface.UiBuilder.OnBuildUi -= BuildUi;
                _pluginInterface.Framework.OnUpdateEvent -= FrameworkOutOfCombatUpdate;
                _pluginInterface.Framework.OnUpdateEvent -= FrameworkInCombatUpdate;
                _pluginInterface.CommandManager.RemoveHandler(Command);
                // TODO TESTING
                
                
                // TODO TESTING END
            }

            if (_controllerPoll.IsEnabled) _controllerPoll.Disable();
            _controllerPoll.Dispose();

            _isDisposed = true;
        }

        ~GentleTouch()
        {
            Dispose(false);
        }

        #endregion
    }
}