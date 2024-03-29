﻿using Application.Abstractions;
using Domain.Common;
using Domain.Dto;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories
{
    public class GroupRepository(ApplicationDbContext context, ICloudinaryRepository cloudinary) : IGroupRepository
    {
        private readonly ApplicationDbContext _context = context;
        private readonly ICloudinaryRepository _cloudinary = cloudinary;

        public async Task<string> CreateGroup(string userId, Group group, IFormFile? picture)
        {
            if (picture is not null)
            {
                var (imageUrl, _, _) = await _cloudinary.Upload(picture, 300, 300);
                group.Picture = imageUrl;
            }

            if (string.IsNullOrWhiteSpace(group.Name))
            {
                return null;
            }

            var newGroup = new Group
            {
                Name = group.Name,
                Description = group.Description,
                CreatorId = userId,
                Picture = group.Picture,
                Created = DateTimeOffset.UtcNow
            };

            _context.Add(newGroup);
            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
            {
                return null;
            }

            var relation = new UserGroup
            {
                UserId = userId,
                GroupId = newGroup.Id
            };

            _context.Add(relation);
            int rowsAffected2 = await _context.SaveChangesAsync();

            if (rowsAffected2 is 0)
            {
                return null;
            }

            return newGroup.Id;
        }

        public async Task<bool> DeleteGroup(string groupId, string userId)
        {
            var group = await _context.Groups.FindAsync(groupId);

            if (group is null || userId != group.CreatorId)
            {
                return false;
            }

            var relations = await _context.UsersGroups
                .Where(x => x.GroupId == groupId)
                .ToListAsync();

            _context.RemoveRange(relations);

            _context.Remove(group);
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<GroupDto?> GetGroup(string? userId, string groupId)
        {
            bool isMember = false;

            if (userId is not null)
            {
                isMember = await _context.UsersGroups.AnyAsync(x => x.UserId == userId && x.GroupId == groupId);
            }

            return await _context.Groups
                .Include(x => x.Users)
                .Include(x => x.Creator)
                .Where(x => x.Id == groupId)
                .Select(x => new GroupDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    CreatorId = x.CreatorId,
                    Picture = x.Picture,
                    Created = x.Created,
                    Users = _context.UsersGroups
                        .Where(y => y.GroupId == x.Id)
                        .OrderByDescending(y => y.Timestamp)
                        .Select(y => new UserDto
                        {
                            Id = y.User.Id,
                            UserName = y.User.UserName,
                            ProfilePicture = y.User.ProfilePicture,
                            LastLogin = y.User.LastLogin
                        }).ToList(),
                    Creator = new UserDto
                    {
                        Id = x.Creator.Id,
                        UserName = x.Creator.UserName,
                        ProfilePicture = x.Creator.ProfilePicture,
                        LastLogin = x.Creator.LastLogin
                    },
                    MembersCount = _context.UsersGroups
                        .Where(y => y.GroupId == x.Id)
                        .Count(),
                    IsMember = isMember
                }).FirstOrDefaultAsync();
        }

        public async Task<DataCountPagesDto<IEnumerable<GroupDto>>> GetGroups(int page, int limit)
        {
            var groups =  await _context.Groups
                .Include(x => x.Users)
                .Include(x => x.Creator)
                .Skip((page - 1) * limit)
                .Take(limit)
                .Select(x => new GroupDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    CreatorId = x.CreatorId,
                    Picture = x.Picture,
                    Created = x.Created,
                    Users = _context.UsersGroups
                        .Where(y => y.GroupId == x.Id)
                        .Select(y => new UserDto {
                            Id = y.User.Id,
                            UserName = y.User.UserName,
                            ProfilePicture = y.User.ProfilePicture
                        }).Take(5).ToList(),
                    Creator = new UserDto
                    {
                        Id = x.Creator.Id,
                        UserName = x.Creator.UserName,
                        ProfilePicture = x.Creator.ProfilePicture
                    }
                }).ToListAsync();

            int count = await _context.Groups.CountAsync();

            int pages = (int)Math.Ceiling((double)count / limit);

            return new DataCountPagesDto<IEnumerable<GroupDto>>
            {
                Data = groups,
                Count = count,
                Pages = pages
            };
        }

        public async Task<DataCountPagesDto<IEnumerable<PostDto>>> GetPostsByGroup(string groupId, string userId, int page, int limit)
        {
            bool isUserMember = await _context.UsersGroups.AnyAsync(x => x.UserId == userId && x.GroupId == groupId);

            if (!isUserMember)
            {
                return null;
            }

            List<PostDto> posts = await _context.Posts
                .Where(x => x.GroupId == groupId)
                .OrderByDescending(x => x.Created)
                .Select(x => new PostDto
                {
                    Id = x.Id,
                    UserId = x.UserId,
                    Created = x.Created,
                    Content = x.Content,
                    Creator = new UserDto
                    {
                        Id = x.Creator.Id,
                        UserName = x.Creator.UserName,
                        ProfilePicture = x.Creator.ProfilePicture
                    },
                    Images = x.Images.Select(x => new ImageDto
                    {
                        Id = x.Id,
                        Url = x.Url
                    }).ToList(),
                    CommentCount = _context.Comments.Count(y => y.PostId == x.Id),
                    ImageCount = _context.Images.Count(y => y.PostId == x.Id)
                })
                .Skip((page - 1) * limit)
                .Take(limit)
                .ToListAsync();

            int count = await _context.Posts.CountAsync(x => x.GroupId == groupId);

            int pages = (int)Math.Ceiling((double)count / limit);

            return new DataCountPagesDto<IEnumerable<PostDto>>
            {
                Data = posts,
                Count = count,
                Pages = pages
            };
        }

        public async Task<OperationResult<UserDto>> JoinGroup(string userId, string groupId)
        {
            bool exists = _context.Groups.Any(x => x.Id == groupId);

            bool userInGroup = _context.UsersGroups.Any(x => x.UserId == userId && x.GroupId == groupId);

            if (!exists || userInGroup)
            {
                OperationResult<UserDto> result = new()
                {
                    Success = false,
                    Message = "Group does not exist or user is already a member"
                };

                return result;
            }

            UserGroup relation = new()
            {
                UserId = userId,
                GroupId = groupId
            };

            _context.Add(relation);
            int rowsAffected =  await _context.SaveChangesAsync();

            if (rowsAffected is 0)
            {
                OperationResult<UserDto> result = new()
                {
                    Success = false,
                    Message = "Could not join group"
                };

                return result;
            }

            UserDto? user = await _context.Users.Where(x => x.Id == userId).Select(x => new UserDto
            {
                Id = x.Id,
                UserName = x.UserName,
                ProfilePicture = x.ProfilePicture,
                LastLogin = x.LastLogin
            }).FirstOrDefaultAsync();

            OperationResult<UserDto> result2 = new()
            {
                Success = true,
                Message = "Joined group",
                Data = user
            };

            return result2;
        }

        public async Task<OperationResult<UserDto>> LeaveGroup(string userId, string groupId)
        {
            bool exists = _context.Groups.Any(x => x.Id == groupId);
            bool userInGroup = _context.UsersGroups.Any(x => x.UserId == userId && x.GroupId == groupId);

            if (!exists || !userInGroup)
            {
                OperationResult<UserDto> result = new()
                {
                    Success = false,
                    Message = "Group does not exist or user is not a member"
                };

                return result;
            }

            var relation = await _context.UsersGroups
                .Where(x => x.UserId == userId && x.GroupId == groupId)
                .FirstOrDefaultAsync();

            _context.Remove(relation);
            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
            {
                OperationResult<UserDto> result = new()
                {
                    Success = false,
                    Message = "Could not leave group"
                };

                return result;
            }

            UserDto? user = await _context.Users.Where(x => x.Id == userId).Select(x => new UserDto
            {
                Id = x.Id,
                UserName = x.UserName,
                ProfilePicture = x.ProfilePicture,
                LastLogin = x.LastLogin
            }).FirstOrDefaultAsync();

            OperationResult<UserDto> result2 = new()
            {
                Success = true,
                Message = "Left group",
                Data = user
            };

            return result2;
        }

        public async Task<bool> UpdateGroup(string groupId, string userId, Group group, IFormFile? picture)
        {
            var groupToUpdate = await _context.Groups.FindAsync(groupId);

            if (groupToUpdate is null || userId != groupToUpdate.CreatorId)
            {
                return false;
            }

            bool anyChanges = false;

            if (picture is not null)
            {
                var (imageUrl, _, _) = await _cloudinary.Upload(picture, 300, 300);
                groupToUpdate.Picture = imageUrl;
                anyChanges = true;
            }

            if (group is not null && !string.IsNullOrWhiteSpace(group.Name))
            {
                groupToUpdate.Name = group.Name;
                anyChanges = true;
            }

            if (group is not null && !string.IsNullOrWhiteSpace(group.Description))
            {
                groupToUpdate.Description = group.Description;
                anyChanges = true;
            }

            if (!anyChanges)
            {
                return false;
            }

            _context.Update(groupToUpdate);

            return await _context.SaveChangesAsync() > 0;
        }
    }
}
