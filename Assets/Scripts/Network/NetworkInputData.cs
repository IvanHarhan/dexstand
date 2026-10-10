using Fusion;
using UnityEngine;

public struct NetworkInputData : INetworkInput
{
    public Vector2 moveInput;
    public Vector2 lookInput;
    public NetworkBool jump;
    public NetworkBool fire;
    public NetworkBool reload;
    public NetworkBool aim;

    /// <summary>Weapon slot picked on this tick, plus one; zero when none was.</summary>
    public byte weapon;

    /// <summary>Emote picked on this tick, plus one; zero when none was.</summary>
    public byte emote;

    /// <summary>Consumable picked on this tick, as a <see cref="Consumable"/>.</summary>
    public byte item;

    /// <summary>The car button, pressed on this tick.</summary>
    public NetworkBool car;

    /// <summary>The driver's pedals, lever and switches (SimpleCar.LocalPedals).</summary>
    public CarPedals pedals;
}

/// <summary>What a driver works with besides the wheel, which rides in moveInput.x.</summary>
public struct CarPedals : INetworkStruct
{
    public byte gas, brake, clutch;   // 0..255, 255 = floored
    public byte switches;             // CarSwitch bits
    public byte lever;                // gear asked for, plus one: 0 R, 1 N, 2.. first and up
    public byte shifts;               // counts lever moves, so the same gear asked twice is tried twice
}

[System.Flags]
public enum CarSwitch : byte
{
    Handbrake = 1, Key = 2, Manual = 4, Left = 8, Right = 16, Hazard = 32, Lights = 64, NoAssists = 128,
}
