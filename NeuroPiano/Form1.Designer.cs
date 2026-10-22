namespace NeuroPiano
{
    partial class fmMain
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Panel pnlKeyboardHost;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
                components.Dispose();

            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.pnlKeyboardHost = new System.Windows.Forms.Panel();
            this.SuspendLayout();
            // 
            // pnlKeyboardHost
            // 
            this.pnlKeyboardHost.BackColor = System.Drawing.Color.Black;
            this.pnlKeyboardHost.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlKeyboardHost.Location = new System.Drawing.Point(0, 0);
            this.pnlKeyboardHost.Name = "pnlKeyboardHost";
            this.pnlKeyboardHost.Size = new System.Drawing.Size(1280, 720);
            this.pnlKeyboardHost.TabIndex = 0;
            // 
            // fmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(13, 13, 14);
            this.ClientSize = new System.Drawing.Size(1280, 720);
            this.Controls.Add(this.pnlKeyboardHost);
            this.MinimumSize = new System.Drawing.Size(800, 450);
            this.Name = "fmMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "NeuroPiano";
            this.Load += new System.EventHandler(this.fmMain_Load);
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.fmMain_FormClosing);
            this.ResumeLayout(false);
        }
    }
}
