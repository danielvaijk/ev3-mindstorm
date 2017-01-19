using System;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;

using Lego.Ev3.Core;
using Lego.Ev3.Desktop;

// Written by Daniel van Dijk. Last modified: 12-09-16

/* NOTES 
 
     1. [ADD] reference System.IO.Ports if you want to have a list of COM Ports appear for the user to choose from. 
     2. Port A has to be the left motor and port B has to be the right motor.
     3. [ADD] Grabber angle guessing
     4. [ADD] Sensor code
     5. [ADD] Motor movement only while button is held down
     6. [ADD] Don't perform any functions on the grabber while it is either opening or closing */

namespace Mindstorm_Bluetooth
{
    public partial class Form1 : Form
    {
        private bool isConnected = false;
        private bool started = false;
        private bool ranCommand = false;
        //private bool obstructed = false;

        private Brick brick;

        private int color = 0;
        private int polarity = 0;

        private float distance = 0;
        private double turnAmount = 3;

        private string lastColor;

        private Stopwatch watch = new Stopwatch();

        private Status currentStatus = Status.Waiting;

        // Represents the current status of the AGV.
        public enum Status
        {
            Waiting,
            PathBlocked,
            FindingPolarity,
            FindingPath,
            MovingToPickup,
            Grabbing,
            MovingToDropoff,
            Dropping,
            MovingToEnd
        }

        // Dictionary with all the text equivalents of each status.
        Dictionary<Status, string> statusValues = new Dictionary<Status, string>();

        public Form1 ()
        {
            InitializeComponent();
        }

        // Called when <Form1> is fully loaded.
        private void Form1_Load(object sender, EventArgs e)
        {
            // Set the text value equivalents to each status.
            statusValues.Add(Status.Waiting, "Waiting");
            statusValues.Add(Status.PathBlocked, "Path is blocked");
            statusValues.Add(Status.FindingPolarity, "Finding polarity");
            statusValues.Add(Status.FindingPath, "Finding path");
            statusValues.Add(Status.MovingToPickup, "Moving to pickup area");
            statusValues.Add(Status.Grabbing, "Grabbing object");
            statusValues.Add(Status.MovingToDropoff, "Moving to dropoff area");
            statusValues.Add(Status.Dropping, "Dropping object");
            statusValues.Add(Status.MovingToEnd, "Moving to end point");

            // Add currently used SerialPorts for selection.
            foreach (string port in SerialPort.GetPortNames())
                comboBox1.Items.Add(port);

            // Default to the first item, if any.
            if (comboBox1.Items.Count > 0)
                comboBox1.SelectedIndex = 0;
        }

        // Connect button.
        private void button1_Click(object sender, EventArgs e)
        {
            if (comboBox1.Items.Count > 0 && comboBox1.SelectedItem != null)
            {
                statusLabel.Text = "Status: Connecting";

                // Establish a new bluetooth connection based on the port <port>.
                ICommunication com = new BluetoothCommunication(comboBox1.SelectedItem.ToString());

                com.ReportReceived += Com_ReportReceived;

                // Setup the connection timer so we can handle connection timeouts.
                System.Timers.Timer connectionTimer = new System.Timers.Timer(1000 * 10);

                connectionTimer.Elapsed += ConnectionTimer_Elapsed;
                connectionTimer.Enabled = true;

                // Set and connect the brick.
                brick = new Brick(com);
                brick.ConnectAsync();

                brick.BrickChanged += Brick_BrickChanged;
            }
        }

        private async void Brick_BrickChanged(object sender, BrickChangedEventArgs e)
        {
            color = e.Ports[InputPort.Two].RawValue;
            distance = e.Ports[InputPort.Three].SIValue;

            label4.Text = string.Format("RAW {0} | SI {1}", e.Ports[InputPort.Two].RawValue, e.Ports[InputPort.Two].SIValue);
            label5.Text = string.Format("RAW {0} | SI {1}", e.Ports[InputPort.Three].RawValue, e.Ports[InputPort.Three].SIValue);
            label6.Text = string.Format("Polarity {0} Turn Vector {1}", polarity, turnAmount);

            label7.Text = "Roboto Status: " + statusValues[currentStatus];
            //label7.Text = "DISTANCE: " + distance;

            if (polarity == 0 && started)
            {
                if (color == 4 || color == 5)
                {
                    // BUG WARNING
                    polarity = (color == 4) ? 1 : -1;
                    currentStatus = Status.MovingToPickup;
                }
            }

            //button9.Enabled = !(obstructed = distance < 6);

            /* if (obstructed && (color == 1 || color == 7))
             {
                 turnAmount = 5;
                 await brick.DirectCommand.StopMotorAsync(OutputPort.B | OutputPort.D, false);
                 currentStatus = Status.PathBlocked;
                 return;
             }*/

            button9.Enabled = (color == 1 || color == 7);

            int speed = GetSpeed(textBox1.Text) * (int)Math.Floor(turnAmount);

            if (currentStatus == Status.Grabbing)
            {
                if (!watch.IsRunning && !ranCommand)
                    watch.Start();

                if (!ranCommand && distance < 5)
                {
                    ranCommand = true;

                    // Stop the timer
                    watch.Stop();

                    // Stop the motors
                    await brick.DirectCommand.StopMotorAsync(OutputPort.B | OutputPort.D, true);

                    // Close claw (grab object)
                    await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.C, GetSpeed(textBox2.Text));
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                    await brick.DirectCommand.StopMotorAsync(OutputPort.C, true);

                    // Go backwards by (time in white) x speed
                    await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B | OutputPort.D, -GetSpeed(textBox1.Text));
                    await Task.Delay(TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds));
                    await brick.DirectCommand.StopMotorAsync(OutputPort.B | OutputPort.D, true);

                    // Turn towards the next entry point for the path.
                    await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, GetSpeed(textBox1.Text) * polarity + 5);

                    await Task.Delay(TimeSpan.FromSeconds(4));

                    await brick.DirectCommand.StopMotorAsync(OutputPort.B, true);
                    await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B | OutputPort.D, GetSpeed(textBox1.Text));

                    currentStatus = Status.FindingPath;
                    return;
                }
            }
            else if (currentStatus == Status.Dropping)
            {
                await brick.DirectCommand.StopMotorAsync(OutputPort.B | OutputPort.D, true);
            }

            switch (color)
            {
                case 1:
                case 7:
                    button9.Text = "Black";

                    if (polarity == 0)
                        return;

                    if (currentStatus == Status.FindingPath)
                    {
                        currentStatus = Status.MovingToDropoff;
                        /*if (currentStatus == Status.Grabbing)
                            currentStatus = Status.MovingToDropoff;
                        else if (currentStatus == Status.Dropping)
                            currentStatus = Status.MovingToEnd;*/
                    }

                    turnAmount = 3;

                    // Go straight.
                    await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.D, GetSpeed(textBox1.Text));
                    await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, GetSpeed(textBox1.Text));
                    break;

                case 4:
                    button9.Text = "Yellow";

                    if (polarity == 0 || currentStatus == Status.FindingPath)
                        return;

                    if (lastColor != "Yellow")
                    {
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.D, GetSpeed(textBox1.Text));
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, GetSpeed(textBox1.Text));
                        lastColor = "Yellow";
                    }

                    turnAmount += 0.6;

                    if (polarity == 1)
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.D, -speed);
                    else
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, -speed);

                    break;

                case 5:
                    button9.Text = "Red";

                    if (polarity == 0 || currentStatus == Status.FindingPath)
                        return;

                    if (lastColor != "Red")
                    {
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.D, GetSpeed(textBox1.Text));
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, GetSpeed(textBox1.Text));
                        lastColor = "Red";
                    }

                    turnAmount += 0.6;

                    if (polarity == 1)
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, -speed);
                    else
                        await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.D, -speed);

                    break;

                case 6:
                    button9.Text = "White";

                    if (polarity == 0)
                        return;

                    if (turnAmount > 9)
                        turnAmount = 3;

                    if (currentStatus == Status.MovingToPickup)
                        currentStatus = Status.Grabbing;

                    if (currentStatus == Status.MovingToDropoff)
                        currentStatus = Status.Dropping;

                    break;
            }
        }

        // Called when the <connectionTimer> has elapsed.
        private void ConnectionTimer_Elapsed (object sender, ElapsedEventArgs e)
        {
            MethodInvoker mi = delegate ()
            {
                // Invoke this on the main thread.
                statusLabel.Text = "Status: Connection timed out";
            };

            Invoke(mi);
        }

        // Called when a full report is received from the brick, confirming a succesfull connection.
        private void Com_ReportReceived(object sender, ReportReceivedEventArgs e)
        {
            MethodInvoker mi = delegate ()
            {
                // Invoke this on the main thread.
                isConnected = true;
                statusLabel.Text = "Status: Connected";

                // Set the modes for each sensor in the sensor ports.
                brick.Ports[InputPort.Two].SetMode(ColorMode.Color);
                brick.Ports[InputPort.Three].SetMode(UltrasonicMode.Centimeters);

                // Change form controls based on new connection status.
                button1.Enabled = false; // Connect
                comboBox1.Enabled = false; // Ports List
                button5.Enabled = true; // Disconnect
            };

            Invoke(mi);
        }

        // Called when the user has released the mouse button from a button. Will stop motors connected to ports A and B.
        private async void StopMotors (object sender, MouseEventArgs e)
        {
            await brick.DirectCommand.StopMotorAsync(OutputPort.A | OutputPort.B, false);
        }

        // Disconnect button.
        private void button5_Click(object sender, EventArgs e)
        {
            // Disconnect the brick.
            brick.Disconnect();

            // Clean up after disconnection.
            statusLabel.Text = "Status: Disconnected";
            isConnected = false;

            // Change form controls based on new connection status.
            button1.Enabled = true; // Connect
            comboBox1.Enabled = true; // Ports List
            button9.Enabled = false; // Start
            button5.Enabled = false; // Disconnect

            // Clean up the variables.
            CleanUp();
        }

        // Returns the speed value based on the <textBox2.Text> string and deals accordingly with
        // invalid parsing (string -> int) exceptions.
        private int GetSpeed (string value)
        {
            int speed = 0;

            if (!int.TryParse(value, out speed))
            {
                statusLabel.Text = "Status: Invalid speed value";
            }
            else if (speed < 0)
            {
                statusLabel.Text = "Status: Invalid speed (value < 0)";
                speed = 0;
            }
            else if (speed == 0)
            {
                statusLabel.Text = "Status: Motor won't move (speed = 0)";
            }

            return speed;
        }

        private async void button9_Click(object sender, EventArgs e)
        {
            if (isConnected)
            {
                started = true;

                currentStatus = Status.FindingPolarity;

                //await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.B, -2);
                await brick.DirectCommand.TurnMotorAtSpeedAsync(OutputPort.D, 1);

                button10.Enabled = true; // Stop button
            }

            //await brick.DirectCommand.PlaySoundAsync(100, @"Project\THEME");
        }

        // Stop button
        private async void button10_Click(object sender, EventArgs e)
        {
            await brick.DirectCommand.StopMotorAsync(OutputPort.B | OutputPort.D, true);
            CleanUp();
        }

        private void CleanUp ()
        {
            started = false;
            polarity = 0;
            currentStatus = Status.Waiting;
        }
    }
}
