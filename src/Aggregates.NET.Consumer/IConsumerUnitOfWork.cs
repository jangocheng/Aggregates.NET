﻿using NServiceBus.ObjectBuilder;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aggregates
{
    public interface IConsumerUnitOfWork
    {
        IBuilder Builder { get; set; }

        void Begin();
        void End(Exception ex = null);
    }
}
