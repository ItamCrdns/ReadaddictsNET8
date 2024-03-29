﻿using Application.Abstractions;
using Application.Interfaces;
using Domain.Common;
using Domain.Dto;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories
{
    public class PostRepository(ApplicationDbContext context, ICloudinaryRepository cloudinary) : IPostRepository
    {
        private readonly ApplicationDbContext _context = context;
        private readonly ICloudinaryRepository _cloudinary = cloudinary;

        public async Task<string> CreatePost(string userId, string? groupId, Post post, IFormFileCollection? images)
        {
            if (groupId is null)
            {
                post.GroupId = null;
            }

            // Check if user is group member before adding this post as a group post
            bool isUserMember = await _context.UsersGroups.AnyAsync(ug => ug.UserId == userId && ug.GroupId == groupId);

            if (isUserMember)
            {
                post.GroupId = groupId;
            }

            if (string.IsNullOrWhiteSpace(post.Content))
            {
                return string.Empty;
            }

            var newPost = new Post
            {
                UserId = userId,
                GroupId = post.GroupId,
                Created = DateTimeOffset.UtcNow,
                Content = post.Content,
                Images = post.Images
            };

            _context.Add(newPost);

            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
            {
                return string.Empty;
            }

            if (images is not null)
            {
                _ = await AddImagesToPost(newPost.Id, userId, images);
            }

            return newPost.Id;
        }

        public async Task<(IEnumerable<string> deleted, IEnumerable<string> notDeleted)> DeleteImageFromPost(string postId, string userId, List<string> imageIds)
        {
            var post = await _context.FindAsync<Post>(postId);

            if (post is null || post.UserId != userId)
                return (Enumerable.Empty<string>(), Enumerable.Empty<string>());

            List<Image> imagesToDelete = await _context.Images
                .Where(i => imageIds.Contains(i.Id) && i.PostId == postId && i.UserId == userId)
                .ToListAsync();

            // where errors its a list of the publicIds that failed to be deleted (if any)
            var (deleted, notDeleted) = await _cloudinary.Destroy(imagesToDelete);

            // after deleting from cloudinary delete from db, but exclude the images that failed to be deleted from cloudinary (if any)
            List<Image> newImagesToDelete = imagesToDelete
                .Where(image => !notDeleted.Contains(image.CloudinaryPublicId))
                .ToList();

            _context.RemoveRange(newImagesToDelete);

            post.Modified = DateTimeOffset.UtcNow;

            _context.Update(post);

            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
                return (Enumerable.Empty<string>(), notDeleted);

            return (deleted, notDeleted);
        }

        public async Task<bool> DeletePost(string userId, string postId)
        {
            Post post = await _context.FindAsync<Post>(postId);

            if (post is null)
            {
                return false;
            }

            if (post.UserId != userId)
            {
                return false;
            }

            _context.Remove(post);
            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
            {
                return false;
            }

            return true;
        }

        public async Task<PostDto?> GetPost(string id)
        {
            return await _context.Posts
                .Where(post => post.Id == id)
                .Include(post => post.Creator)
                //.Include(post => post.Comments)
                .Select(post => new PostDto
                {
                    Id = post.Id,
                    UserId = post.UserId,
                    Created = post.Created,
                    Content = post.Content,
                    Creator = new UserDto
                    {
                        Id = post.Creator.Id,
                        UserName = post.Creator.UserName,
                        ProfilePicture = post.Creator.ProfilePicture
                    },
                    Images = post.Images.Select(image => new ImageDto
                    {
                        Id = image.Id,
                        Url = image.Url
                    }).ToList(),
                    CommentCount = post.Comments.Count,
                    Group = new GroupDto
                    {
                        Id = post.Group.Id,
                        Name = post.Group.Name,
                        Picture = post.Group.Picture
                    }
                }).FirstOrDefaultAsync();
        }

        public async Task<DataCountPagesDto<List<PostDto>>> GetPosts(int page, int limit)
        {
            var posts = await _context.Posts
                .Include(post => post.Creator)
                .Where(x => x.GroupId == null)
                .OrderByDescending(post => post.Created)
                .Skip((page - 1) * limit)
                .Take(limit)
                .Select(post => new PostDto
                {
                    Id = post.Id,
                    UserId = post.UserId,
                    Created = post.Created,
                    Content = post.Content,
                    Creator = new UserDto
                    {
                        Id = post.Creator.Id,
                        UserName = post.Creator.UserName,
                        ProfilePicture = post.Creator.ProfilePicture
                    },
                    Images = post.Images.Select(image => new ImageDto
                    {
                        Id = image.Id,
                        Url = image.Url
                    }).Take(5).ToList(),
                    CommentCount = _context.Comments.Count(comment => comment.PostId == post.Id),
                    ImageCount = _context.Images.Count(image => image.PostId == post.Id)
                })
                .ToListAsync();

            int count = await _context.Posts.CountAsync(post => post.GroupId == null);

            int pages = (int)Math.Ceiling(count / (double)limit);

            return new DataCountPagesDto<List<PostDto>>
            {
                Data = posts,
                Count = count,
                Pages = pages
            };
        }

        public async Task<(bool, string)> UpdatePostContent(string postId, string userId, string content)
        {
            Post? post = await _context.Posts
                .Include(post => post.Creator)
                .Include(post => post.Images)
                .FirstOrDefaultAsync(post => post.Id == postId);

            if (post is null || post.UserId != userId)
                return (false, string.Empty);

            post.Content = content;
            post.Modified = DateTimeOffset.UtcNow;

            _context.Update(post);
            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
                return (false, string.Empty);

            return (true, content);
        }

        public async Task<IEnumerable<ImageDto>> AddImagesToPost(string postId, string userId, IFormFileCollection images)
        {
            var post = await _context.FindAsync<Post>(postId);

            if (post is null || post.UserId != userId)
                return [];

            // Get a list of the images that were uploaded to Cloudinary and add them to the post
            List<(string imageUrl, string publicId, string result)> imgs = await _cloudinary.UploadMany(images);

            if (imgs.Count == 0)
            {
                return [];
            }

            List<Image> newPostImages = imgs.Select(image => new Image
            {
                PostId = postId,
                UserId = userId,
                Url = image.imageUrl,
                CloudinaryPublicId = image.publicId,
                Created = DateTimeOffset.UtcNow
            }).ToList();

            _context.Images.AddRange(newPostImages);

            post.Modified = DateTimeOffset.UtcNow;

            _context.Update(post);

            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
                return [];

            return newPostImages.Select(i => new ImageDto
            {
                Id = i.Id,
                Url = i.Url
            }).ToList();
        }

        public async Task<DataCountPagesDto<IEnumerable<PostDto>>> GetPostsByUser(string username, int page, int limit)
        {
            User? user = await _context.Users.FirstOrDefaultAsync(user => user.UserName == username);

            if (user is null)
            {
                return new DataCountPagesDto<IEnumerable<PostDto>>
                {
                    Data = Enumerable.Empty<PostDto>(),
                    Count = 0,
                    Pages = 0
                };
            }

            var posts = await _context.Posts
                .Where(post => post.UserId == user.Id)
                .Include(post => post.Creator)
                .OrderByDescending(post => post.Created)
                .Skip((page - 1) * limit)
                .Take(limit)
                .Select(post => new PostDto
                {
                    Id = post.Id,
                    UserId = post.UserId,
                    Created = post.Created,
                    Content = post.Content,
                    Creator = new UserDto
                    {
                        Id = post.Creator.Id,
                        UserName = post.Creator.UserName,
                        ProfilePicture = post.Creator.ProfilePicture
                    },
                    Images = post.Images.Select(image => new ImageDto
                    {
                        Id = image.Id,
                        Url = image.Url
                    }).Take(5).ToList(),
                    CommentCount = _context.Comments.Count(comment => comment.PostId == post.Id),
                    ImageCount = _context.Images.Count(image => image.PostId == post.Id)
                })
                .ToListAsync();

            int count = await _context.Posts.CountAsync(post => post.UserId == user.Id);

            int pages = (int)Math.Ceiling(count / (double)limit);

            return new DataCountPagesDto<IEnumerable<PostDto>>
            {
                Data = posts,
                Count = count,
                Pages = pages
            };
        }

        public async Task<UpdatedPost> UpdateAll(string postId, string userId, string? content, IFormFileCollection? newImages, List<string>? imageIdsToRemove)
        {
            Post? post = await _context.FindAsync<Post>(postId);

            if (post is null || post.UserId != userId)
                return new UpdatedPost();

            if (!string.IsNullOrWhiteSpace(content))
            {
                post.Content = content;
            }

            List<ImageDto> addedImages = [];
            if (newImages is not null && newImages.Count > 0)
            {
                List<(string imageUrl, string publicId, string result)> imgs = await _cloudinary.UploadMany(newImages);

                List<Image> newPostImages = imgs.Select(image => new Image
                {
                    PostId = postId,
                    UserId = userId,
                    Url = image.imageUrl,
                    CloudinaryPublicId = image.publicId,
                    Created = DateTimeOffset.UtcNow
                }).ToList();

                _context.Images.AddRange(newPostImages);

                List<ImageDto>? newPostImagesDto = newPostImages.Select(i => new ImageDto
                {
                    Id = i.Id,
                    Url = i.Url
                }).ToList();

                addedImages.AddRange(newPostImagesDto);
            }

            List<string> removedImages = [];
            if (imageIdsToRemove is not null && imageIdsToRemove.Count > 0)
            {
                List<Image> imagesToDelete = await _context.Images
                    .Where(i => imageIdsToRemove.Contains(i.Id) && i.PostId == postId && i.UserId == userId)
                    .ToListAsync();

                // where errors its a list of the publicIds that failed to be deleted (if any)
                var (deleted, notDeleted) = await _cloudinary.Destroy(imagesToDelete);

                // after deleting from cloudinary delete from db, but exclude the images that failed to be deleted from cloudinary (if any)
                List<Image> newImagesToDelete = imagesToDelete
                    .Where(image => !notDeleted.Contains(image.CloudinaryPublicId))
                    .ToList();

                _context.RemoveRange(newImagesToDelete);
                removedImages.AddRange(deleted);
            }

            post.Modified = DateTimeOffset.UtcNow;

            _context.Update(post);

            int rowsAffected = await _context.SaveChangesAsync();

            if (rowsAffected is 0)
                return new UpdatedPost();

            return new UpdatedPost
            {
                NewContent = post.Content,
                AddedImages = addedImages,
                RemovedImages = removedImages
            };
        }
    }
}
