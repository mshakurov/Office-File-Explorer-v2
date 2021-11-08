﻿using Office_File_Explorer.Helpers;
using System;
using System.Windows.Forms;

namespace Office_File_Explorer.WinForms
{
    public partial class FrmExcelModify : Form
    {
        public AppUtilities.ExcelModifyCmds xlModCmd = new AppUtilities.ExcelModifyCmds();

        public FrmExcelModify()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (rdoDelLinks.Checked)
            {
                xlModCmd = AppUtilities.ExcelModifyCmds.DelLinks;
            }

            if (rdoDelEmbedLinks.Checked)
            {
                xlModCmd = AppUtilities.ExcelModifyCmds.DelEmbeddedLinks;
            }

            if (rdoDelComments.Checked)
            {
                xlModCmd = AppUtilities.ExcelModifyCmds.DelComments;
            }

            if (rdoConvertToXlsm.Checked)
            {
                xlModCmd = AppUtilities.ExcelModifyCmds.ConvertXlsmToXlsx;
            }

            if (rdoConvertStrict.Checked)
            {
                xlModCmd = AppUtilities.ExcelModifyCmds.ConvertStrictToXlsx;
            }

            Close();
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
