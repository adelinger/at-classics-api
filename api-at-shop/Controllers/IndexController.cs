﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace api_at_shop.Controllers
{
    [Route("/")]
    public class IndexController : Controller
    {
        // GET: api/values
        [HttpGet]
        [AllowAnonymous]
        public ActionResult Get()
        {
            return Ok("At-classics api");
        }
    }
}