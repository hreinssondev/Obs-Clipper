using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OBSClipper;

// The first-launch window: runs Setup and then explains the one manual step in Medal.
sealed class SetupForm : Form
{
    readonly Setup setup;
    readonly List<(Setup.Step Step, Label Icon, Label Title, Label Detail)> rows = new();
    readonly ModernButton action = new() { Text = "Set up", Kind = ButtonKind.Primary, AutoSize = true, MinimumSize = new Size(120, 32), Margin = Padding.Empty };
    readonly ModernButton skip = new() { Text = "Skip for now", Kind = ButtonKind.Ghost, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
    readonly RoundedPanel medalCard;
    bool running, finished;

    public bool Completed { get; private set; }

    public SetupForm(Setup setup, string clipsFolder, Icon icon)
    {
        this.setup = setup;
        Text = "OBS Clipper setup";
        Icon = icon;
        Font = Theme.Body;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 600);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(26, 22, 26, 20) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label { Text = "Let's get you set up", AutoSize = true, Font = new Font(Theme.Strong.FontFamily, 15f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) });
        root.Controls.Add(Muted("OBS Clipper installs and sets up everything it needs. This takes a few minutes. Click Yes if Windows asks for permission to install OBS."));

        var steps = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = new Point(14, 12), Margin = Padding.Empty };
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        foreach (var step in setup.Steps)
        {
            var icon2 = new Label { AutoSize = true, Font = new Font(Theme.Strong.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 0) };
            var title = new Label { Text = step.Title, AutoSize = true, Font = Theme.Strong, Margin = new Padding(0, 8, 0, 0) };
            var detail = new Label { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(420, 0), Margin = new Padding(0, 2, 0, 6) };
            steps.Controls.Add(icon2, 0, row);
            steps.Controls.Add(title, 1, row++);
            steps.Controls.Add(detail, 1, row++);
            rows.Add((step, icon2, title, detail));
        }
        var stepsCard = new RoundedPanel { Dock = DockStyle.Top, Margin = new Padding(0, 16, 0, 0) };
        stepsCard.Controls.Add(steps);
        root.Controls.Add(stepsCard);

        // Medal can't be set up from outside, so the user does this part.
        var medal = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(14, 12), Margin = Padding.Empty };
        medal.Controls.Add(new Label { Text = "Last step: add the clips folder to Medal", AutoSize = true, Font = Theme.Strong, Margin = new Padding(0, 0, 0, 6) });
        medal.Controls.Add(Muted("In Medal, open Settings → PC Storage → External Recorders Import Locations → Sync an External Recorder, and pick this folder. Don't pick it as Medal's own capture folder."));
        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        pathRow.Controls.Add(new Label { Text = clipsFolder, AutoSize = true, Margin = new Padding(0, 8, 10, 0) });
        var copy = new ModernButton { Text = "Copy folder path", AutoSize = true, Margin = Padding.Empty };
        copy.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(clipsFolder);
            Clipboard.SetText(clipsFolder);
            copy.Text = "Copied";
        };
        pathRow.Controls.Add(copy);
        medal.Controls.Add(pathRow);
        medalCard = new RoundedPanel { Dock = DockStyle.Top, Margin = new Padding(0, 14, 0, 0), Visible = false };
        medalCard.Controls.Add(medal);
        root.Controls.Add(medalCard);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 0, 26, 20) };
        buttons.Controls.Add(action);
        buttons.Controls.Add(skip);

        Controls.Add(root);
        Controls.Add(buttons);

        // Cards size to their content once it's laid out.
        Load += (_, _) =>
        {
            stepsCard.Height = steps.PreferredSize.Height + 24;
            medalCard.Height = medal.PreferredSize.Height + 24;
        };

        action.Click += async (_, _) =>
        {
            if (finished)
            {
                Close();
                return;
            }
            running = true;
            action.Enabled = skip.Enabled = false;
            action.Text = "Setting up…";
            bool ok = await setup.RunAsync();
            running = false;
            action.Enabled = skip.Enabled = true;
            if (ok)
            {
                // Counts as done however the window is closed.
                Completed = finished = true;
                action.Text = "Finish";
                skip.Visible = false;
                medalCard.Visible = true;
            }
            else
            {
                action.Text = "Try again";
            }
        };
        skip.Click += (_, _) => Close();
        setup.Changed += RefreshRows;
        RefreshRows();
    }

    void RefreshRows()
    {
        foreach (var (step, icon, title, detail) in rows)
        {
            (icon.Text, icon.ForeColor) = step.State switch
            {
                Setup.StepState.Done => ("✓", Theme.Success),
                Setup.StepState.Failed => ("✕", Theme.Danger),
                Setup.StepState.Working => ("●", Theme.Accent),
                _ => ("○", Theme.Off),
            };
            title.ForeColor = step.State == Setup.StepState.Pending ? Theme.Muted : Theme.Text;
            detail.Text = step.Detail;
            detail.ForeColor = step.State == Setup.StepState.Failed ? Theme.Danger : Theme.Muted;
            detail.Visible = step.Detail.Length > 0;
        }
        var card = rows.Count > 0 ? rows[0].Icon.Parent : null;
        if (card?.Parent != null) card.Parent.Height = card.PreferredSize.Height + 24;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // OBS may be mid-restart; let setup finish first.
        if (running) e.Cancel = true;
        base.OnFormClosing(e);
    }

    static Label Muted(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 4) };
}
