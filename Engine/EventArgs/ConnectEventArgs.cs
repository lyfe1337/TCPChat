﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Engine
{
  [Serializable]
  public class ConnectEventArgs : EventArgs
  {
    public Exception Error { get; set; }
  }
}
