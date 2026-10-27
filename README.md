# LeGo my cursor!

The Lenovo Legion Go 2 has a little trackpad on the right controller. This is great!  
Sometimes, when you lift your finger off the cursor will go shooting off to the bottom of the screen. This sucks.

The issue is that Lenovo's firmware is setting some coordinates to a large value that means "no position" before setting another bit that means "not being touched". 
<details>
<summary><b>Root cause</b></summary>

On finger lift the firmware intermittently emits two consecutive reports for the lifting contact with Tip Switch = 1 and X = Y = 0xFFFF.  
The Windows Precision Touchpad stack clamps each coordinate to Logical Maximum, so the contact is reported at (1024, 1024). Because Tip Switch is set, that is a valid contact that moved from its last position to the far corner within one frame, and the stack emits the corresponding relative pointer move.

</details>

## What do we do?

[Windows has some shit](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/overview-of-the-umdf) that [allows other drivers to change stuff](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/installing-a-umdf-filter-driver). LeGo my cursor! is a **UMDF 2 upper device filter** that lets us patch the times the firmware reports an out-of-bound move so it stays where it is supposed to.

## What's the catch?

We need to install a driver on your system and Windows really doesn't want you to do that with some random code. Something in the chain needs to be **signed**, which usually takes $$$ and a business and a whole lot of paperwork.

The easier/cheaper thing is for you to generate a certificate on your own device, trust it, then sign the software with your own keys.  

This repo has both the driver code and an installer/config program to handle all the necessary steps for you. All of the code used in the releases is available in the repo, and all releases are built here in the open. 

### Installing

Download the latest release from [instructions to releases here]

[other install instructions here]

### Windows is blocking something!

[section about Smart App Control]
