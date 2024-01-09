﻿using Application.Abstractions;
using Domain.Dto;
using Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ReadaddictsNET8.Endpoints
{
    public static class Groups
    {
        public static void AddGroupsEndpoints(this IEndpointRouteBuilder routes)
        {
            RouteGroupBuilder groups = routes.MapGroup("/api/v1/groups");

            groups.MapGet("/all", GetGroups);
            groups.MapGet("{id}", GetGroup);
            groups.MapPost("/create", CreateGroup).RequireAuthorization();
            groups.MapPatch("{id}", UpdateGroup).RequireAuthorization();
        }

        private static string GetUserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
        public static async Task<Results<Ok<List<GroupDto>>, NotFound>> GetGroups([FromServices] IGroupRepository groupRepository, int page, int limit)
        {
            List<GroupDto> groups = await groupRepository.GetGroups(page, limit);

            if (groups.Count == 0)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.Ok(groups);
        }
        public static async Task<Results<Ok<GroupDto>, NotFound>> GetGroup([FromServices] IGroupRepository groupRepository, string id)
        {
            GroupDto? group = await groupRepository.GetGroup(id);

            if (group is null)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.Ok(group);
        }
        public static async Task<Results<Ok<string>, BadRequest>> CreateGroup([FromServices] IGroupRepository groupRepository, ClaimsPrincipal user, [FromForm] Group group, [FromForm] IFormFile? picture)
        {
            string groupId = await groupRepository.CreateGroup(GetUserId(user), group, picture);

            if (groupId is null)
            {
                return TypedResults.BadRequest();
            }

            return TypedResults.Ok(groupId);
        }

        public static async Task<Results<Ok, BadRequest>> UpdateGroup([FromServices] IGroupRepository groupRepository, ClaimsPrincipal user, string id, [FromForm] Group group, [FromForm] IFormFile? picture)
        {
            bool updated = await groupRepository.UpdateGroup(id, GetUserId(user), group, picture);

            if (!updated)
            {
                return TypedResults.BadRequest();
            }

            return TypedResults.Ok();
        }
    }
}

